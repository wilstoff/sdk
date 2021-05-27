// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.DotNet.Cli.Telemetry;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Tools.MSBuild
{
    public class MSBuildForwardingApp
    {
        internal const string TelemetrySessionIdEnvironmentVariableName = "DOTNET_CLI_TELEMETRY_SESSIONID";

        private MSBuildForwardingAppWithoutLogging _forwardingAppWithoutLogging;

        private static IEnumerable<string> ConcatTelemetryLogger(IEnumerable<string> argsToForward)
        {
            if (Telemetry.CurrentSessionId != null)
            {
                try
                {
                    Type loggerType = typeof(MSBuildLogger);
                    Type forwardingLoggerType = typeof(MSBuildForwardingLogger);

                    return argsToForward
                        .Concat(new[]
                        {
                            $"-distributedlogger:{loggerType.FullName},{loggerType.GetTypeInfo().Assembly.Location}*{forwardingLoggerType.FullName},{forwardingLoggerType.GetTypeInfo().Assembly.Location}"
                        });
                }
                catch (Exception)
                {
                    // Exceptions during telemetry shouldn't cause anything else to fail
                }
            }
            return argsToForward;
        }

        public MSBuildForwardingApp(IEnumerable<string> argsToForward, string msbuildPath = null)
        {
            _forwardingAppWithoutLogging = new MSBuildForwardingAppWithoutLogging(
                ConcatTelemetryLogger(argsToForward),
                msbuildPath);

            // Add the performance log location to the environment of the target process.
            if (PerformanceLogManager.Instance != null && !string.IsNullOrEmpty(PerformanceLogManager.Instance.CurrentLogDirectory))
            {
                EnvironmentVariable(PerformanceLogManager.PerfLogDirEnvVar, PerformanceLogManager.Instance.CurrentLogDirectory);
            }
        }

        public void EnvironmentVariable(string name, string value)
        {
            _forwardingAppWithoutLogging.EnvironmentVariable(name, value);
        }

        public ProcessStartInfo GetProcessStartInfo()
        {
            EnvironmentVariable(TelemetrySessionIdEnvironmentVariableName, Telemetry.CurrentSessionId);

            return _forwardingAppWithoutLogging.GetProcessStartInfo();
        }

        /// <summary>
        /// Test hook returning concatenated and escaped command line arguments that would be passed to MSBuild.
        /// </summary>
        internal string GetArgumentsToMSBuild()
        {
            var argumentsUnescaped = _forwardingAppWithoutLogging.GetAllArguments();
            return Cli.Utils.ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(argumentsUnescaped);
        }

        public virtual int Execute()
        {
            // Ignore Ctrl-C for the remainder of the command's execution
            // Forwarding commands will just spawn the child process and exit
            Console.CancelKeyPress += (sender, e) => { e.Cancel = true; };

            int exitCode;
            if (_forwardingAppWithoutLogging.ExecuteMSBuildOutOfProc)
            {
                ProcessStartInfo startInfo = GetProcessStartInfo();

                PerformanceLogEventSource.Log.LogMSBuildStart(startInfo.FileName, startInfo.Arguments);
                exitCode = startInfo.Execute();
                PerformanceLogEventSource.Log.MSBuildStop(exitCode);
            }
            else
            {
                string[] arguments = _forwardingAppWithoutLogging.GetAllArguments();

                if (Env.GetEnvironmentVariableAsBool("DOTNET_CLI_RUN_MSBUILD_IN_SERVER"))
                {
                    PerformanceLogEventSource.Log.LogMSBuildStart("Server: " + _forwardingAppWithoutLogging.MSBuildPath, ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(arguments));
                    exitCode = ExecuteInMSBuildServer(arguments);
                } 
                else
                {
                    PerformanceLogEventSource.Log.LogMSBuildStart("In process: " + _forwardingAppWithoutLogging.MSBuildPath, ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(arguments));
                    exitCode = _forwardingAppWithoutLogging.ExecuteInProc(arguments);
                }

                PerformanceLogEventSource.Log.MSBuildStop(exitCode);
            }

            return exitCode;
        }

        private int ExecuteInMSBuildServer(string[] arguments)
        {
            int exitCode;
            string msBuildLocation = _forwardingAppWithoutLogging.MSBuildPath;

            // create forwarding app for msbuild server
            var msBuildServerForwardingApp = _forwardingAppWithoutLogging.GetMSBuildServerForwardingApp(ConcatTelemetryLogger(new[]
            {
                "/nologo",
                "/nodemode:8",
                "/nodeReuse:true"
            }).ToArray());
            ProcessStartInfo msBuildServerStartInfo = msBuildServerForwardingApp.GetProcessStartInfo();

            var handshake = new EntryNodeHandshake(
                GetHandshakeOptions(),
                msBuildLocation);

            string pipeName = GetPipeNameOrPath("MSBuildServer-" + handshake.ComputeHash());

            // check if server is running
            var serverRunningMutexName = $@"Global\server-running-{pipeName}";
            var serverBusyMutexName = $@"Global\server-busy-{pipeName}";
            var serverWasAlreadyRunning = CommunicationsUtilities.ServerNamedMutex.WasOpen(serverRunningMutexName);
            if (!serverWasAlreadyRunning)
            {
                Process msbuildProcess = LaunchNode(msBuildServerStartInfo);
            }

            var serverWasBusy = CommunicationsUtilities.ServerNamedMutex.WasOpen(serverBusyMutexName);
            if (serverWasBusy)
            {
                Console.WriteLine("Server is busy - that IS unexpected - we shall fallback to former behavior.");
                throw new InvalidOperationException("Server is busy - that IS unexpected - we shall fallback to former behavior. NOT IMPLEMENTED YET");
            }

            // connect to it
            NamedPipeClientStream nodeStream = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            nodeStream.Connect(serverWasAlreadyRunning && !serverWasBusy ? 1_000 : 20_000);

            int[] handshakeComponents = handshake.RetrieveHandshakeComponents();
            for (int i = 0; i < handshakeComponents.Length; i++)
            {
                CommunicationsUtilities.Trace("Writing handshake part {0} ({1}) to pipe {2}", i, handshakeComponents[i], pipeName);
                WriteIntForHandshake(nodeStream, handshakeComponents[i]);
            }

            // This indicates that we have finished all the parts of our handshake; hopefully the endpoint has as well.
            WriteIntForHandshake(nodeStream, EntryNodeHandshake.EndOfHandshakeSignal);

            CommunicationsUtilities.Trace("Reading handshake from pipe {0}", pipeName);

            ReadEndOfHandshakeSignal(nodeStream, timeout: 1000);

            CommunicationsUtilities.Trace("Successfully connected to pipe {0}...!", pipeName);

            Dictionary<string, string> envVars = new();
            var vars = Environment.GetEnvironmentVariables();
            foreach (var key in vars.Keys)
            {
                envVars[(string) key] = (string) vars[key];
            }

            foreach (var pair in msBuildServerStartInfo.Environment)
            {
                envVars[pair.Key] = pair.Value;
            }

            var buildCommand = new EntryNodeCommand(
                commandLine: '"' + msBuildLocation + '"' + " " + string.Join(' ', arguments),
                startupDirectory: Directory.GetCurrentDirectory(),
                buildProcessEnvironment: envVars,
                CultureInfo.CurrentCulture,
                CultureInfo.CurrentUICulture);

            buildCommand.WriteToStream(nodeStream);

            CommunicationsUtilities.Trace("Build command send...");

            while (true)
            {
                var packet = ReadPacket(nodeStream);
                if (packet is EntryNodeConsoleWrite consoleWrite)
                {
                    Console.ForegroundColor = consoleWrite.Foreground;
                    Console.BackgroundColor = consoleWrite.Background;
                    switch (consoleWrite.OutputType)
                    {
                        case 1:
                            Console.Write(consoleWrite.Text);
                            break;
                        case 2:
                            Console.Error.Write(consoleWrite.Text);
                            break;
                        default:
                            throw new InvalidOperationException($"Unexpected console output type {consoleWrite.OutputType}");
                    }
                }
                else if (packet is EntryNodeResponse response)
                {
                    CommunicationsUtilities.Trace($"Build response received: exit code {response.ExitCode}, exit type '{response.ExitType}'");
                    exitCode = response.ExitCode;
                    break;
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected packet type {packet.GetType().Name}");
                }
            }

            return exitCode;
        }

        // vvvvvvvvvvvv TO BE REFACTORED vvvvvvvvvvvv

        private static object ReadPacket(NamedPipeClientStream nodeStream)
        {
            var headerBytes = new byte[5];
            var readBytes = nodeStream.Read(headerBytes, 0, 5);
            if (readBytes != 5)
                throw new InvalidOperationException("Not enough header bytes read from named pipe");
            byte packetType = headerBytes[0];
            int bodyLen = (headerBytes[1] << 00) |
                          (headerBytes[2] << 08) |
                          (headerBytes[3] << 16) |
                          (headerBytes[4] << 24);
            var bodyBytes = new byte[bodyLen];
            readBytes = nodeStream.Read(bodyBytes, 0, bodyLen);
            if (readBytes != bodyLen)
                throw new InvalidOperationException($"Not enough bytes read to read body: expected {bodyLen}, read {readBytes}");

            var ms = new MemoryStream(bodyBytes);
            switch (headerBytes[0])
            {
                case EntryNodeResponse.PacketType:
                    return EntryNodeResponse.DeserializeFromStream(ms);
                case EntryNodeConsoleWrite.PacketType:
                    return EntryNodeConsoleWrite.DeserializeFromStream(ms);
            }

            throw new InvalidOperationException($"Unexpected packet type {headerBytes[0]:X}");
        }

        /// <summary>
        /// Extension method to write a series of bytes to a stream
        /// </summary>
        internal static void WriteIntForHandshake(PipeStream stream, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);

            // We want to read the long and send it from left to right (this means big endian)
            // if we are little endian we need to reverse the array to keep the left to right reading
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            stream.Write(bytes, 0, bytes.Length);
        }

        internal static void ReadEndOfHandshakeSignal(PipeStream stream, int timeout)
        {
            // Accept only the first byte of the EndOfHandshakeSignal
            int valueRead = ReadIntForHandshake(stream, timeout: 1000);

            if (valueRead != EntryNodeHandshake.EndOfHandshakeSignal)
            {
                CommunicationsUtilities.Trace("Expected end of handshake signal but received {0}. Probably the host is a different MSBuild build.", valueRead);
                throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// Extension method to read a series of bytes from a stream.
        /// If specified, leading byte matches one in the supplied array if any, returns rejection byte and throws IOException.
        /// </summary>
        internal static int ReadIntForHandshake(PipeStream stream, int timeout)
        {
            byte[] bytes = new byte[4];

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Enforce a minimum timeout because the Windows code can pass
                // a timeout of 0 for the connection, but that doesn't work for
                // the actual timeout here.
                timeout = Math.Max(timeout, 50);

                // A legacy MSBuild.exe won't try to connect to MSBuild running
                // in a dotnet host process, so we can read the bytes simply.
                var readTask = stream.ReadAsync(bytes, 0, bytes.Length);

                // Manual timeout here because the timeout passed to Connect() just before
                // calling this method does not apply on UNIX domain socket-based
                // implementations of PipeStream.
                // https://github.com/dotnet/corefx/issues/28791
                if (!readTask.Wait(timeout))
                {
                    throw new IOException(string.Format(CultureInfo.InvariantCulture, "Did not receive return handshake in {0}ms", timeout));
                }

                readTask.GetAwaiter().GetResult();
            }
            else
            {
                // Legacy approach with an early-abort for connection attempts from ancient MSBuild.exes
                for (int i = 0; i < bytes.Length; i++)
                {
                    int read = stream.ReadByte();

                    if (read == -1)
                    {
                        // We've unexpectly reached end of stream.
                        // We are now in a bad state, disconnect on our end
                        throw new IOException(String.Format(CultureInfo.InvariantCulture, "Unexpected end of stream while reading for handshake"));
                    }

                    bytes[i] = Convert.ToByte(read);
                }
            }

            int result;

            try
            {
                // We want to read the long and send it from left to right (this means big endian)
                // If we are little endian the stream has already been reversed by the sender, we need to reverse it again to get the original number
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }

                result = BitConverter.ToInt32(bytes, 0 /* start index */);
            }
            catch (ArgumentException ex)
            {
                throw new IOException(String.Format(CultureInfo.InvariantCulture, "Failed to convert the handshake to big-endian. {0}", ex.Message));
            }

            return result;
        }

        private static HandshakeOptions GetHandshakeOptions()
        {
            var options = HandshakeOptions.NodeReuse;
            if (Marshal.SizeOf<IntPtr>() == 8)
            {
                options |= HandshakeOptions.X64;
            }

            return options;
        }

        private static string GetPipeNameOrPath(string pipeName)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // If we're on a Unix machine then named pipes are implemented using Unix Domain Sockets.
                // Most Unix systems have a maximum path length limit for Unix Domain Sockets, with
                // Mac having a particularly short one. Mac also has a generated temp directory that
                // can be quite long, leaving very little room for the actual pipe name. Fortunately,
                // '/tmp' is mandated by POSIX to always be a valid temp directory, so we can use that
                // instead.
                return Path.Combine("/tmp", pipeName);
            }

            return pipeName;
        }

        private static Process LaunchNode(ProcessStartInfo processStartInfo)
        {
            // Redirect the streams of worker nodes so that this 
            // parent doesn't wait on idle worker nodes to close streams
            // after the build is complete.
            processStartInfo.RedirectStandardInput = false;
            processStartInfo.RedirectStandardOutput = false;
            processStartInfo.RedirectStandardError = false;
            processStartInfo.CreateNoWindow = true;
            processStartInfo.UseShellExecute = false;

            Process process;
            try
            {
                process = Process.Start(processStartInfo);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("MSBuild server node failed to lunch", ex);
            }

            return process;
        }
    }

    internal class CommunicationsUtilities
    {
        public static void Trace(string format, params object[] args)
        {
            //Console.WriteLine(format, args);
            Debug.WriteLine(format, args);
        }

        internal static IServerMutex OpenOrCreateMutex(string name, out bool createdNew)
        {
            // TODO: verify it is not needed anymore
            //if (PlatformInformation.IsRunningOnMono)
            //{
            //    return new ServerFileMutexPair(name, initiallyOwned: true, out createdNew);
            //}
            //else

            return new ServerNamedMutex(name, out createdNew);
        }

        internal interface IServerMutex : IDisposable
        {
            bool TryLock(int timeoutMs);
            bool IsDisposed { get; }
        }

        internal sealed class ServerNamedMutex : IServerMutex
        {
            public readonly Mutex ServerMutex;

            public bool IsDisposed { get; private set; }
            public bool IsLocked { get; private set; }

            public ServerNamedMutex(string mutexName, out bool createdNew)
            {
                ServerMutex = new Mutex(
                    initiallyOwned: true,
                    name: mutexName,
                    createdNew: out createdNew
                );
                if (createdNew)
                    IsLocked = true;
            }

            public static bool WasOpen(string mutexName)
            {
                try
                {
                    // we can't use TryOpenExisting as it is not supported in net3.5
                    using var m = Mutex.OpenExisting(mutexName);
                    return true;
                }
                catch
                {
                    // In the case an exception occurred trying to open the Mutex then 
                    // the assumption is that it's not open.
                    return false;
                }
            }

            public bool TryLock(int timeoutMs)
            {
                if (IsDisposed)
                    throw new ObjectDisposedException("Mutex");
                if (IsLocked)
                    throw new InvalidOperationException("Lock already held");
                return IsLocked = ServerMutex.WaitOne(timeoutMs);
            }

            public void Dispose()
            {
                if (IsDisposed)
                    return;
                IsDisposed = true;

                try
                {
                    if (IsLocked)
                        ServerMutex.ReleaseMutex();
                }
                finally
                {
                    (ServerMutex as IDisposable).Dispose();
                    IsLocked = false;
                }
            }
        }
    }

    /// <summary>
    /// Enumeration of all possible (currently supported) options for handshakes.
    /// </summary>
    [Flags]
    internal enum HandshakeOptions
    {
        None = 0,

        /// <summary>
        /// Process is a TaskHost
        /// </summary>
        TaskHost = 1,

        /// <summary>
        /// Using the 2.0 CLR
        /// </summary>
        CLR2 = 2,

        /// <summary>
        /// 64-bit Intel process
        /// </summary>
        X64 = 4,

        /// <summary>
        /// Node reuse enabled
        /// </summary>
        NodeReuse = 8,

        /// <summary>
        /// Building with BelowNormal priority
        /// </summary>
        LowPriority = 16,

        /// <summary>
        /// Building with administrator privileges
        /// </summary>
        Administrator = 32
    }

    internal class EntryNodeHandshake
    {
        /// <summary>
        /// The version of the handshake. This should be updated each time the handshake is altered.
        /// </summary>
        readonly int _version = 0x101;

        readonly int _options;
        readonly int _salt;
        readonly int _fileVersionMajor;
        readonly int _fileVersionMinor;
        readonly int _fileVersionBuild;
        readonly int _fileVersionRevision;

        internal EntryNodeHandshake(HandshakeOptions nodeType, string msBuildLocation)
        {
            // We currently use 6 bits of this 32-bit integer. Very old builds will instantly reject any handshake that does not start with F5 or 06; slightly old builds always lead with 00.
            // This indicates in the first byte that we are a modern build.
            _options = (int)nodeType;
            string handshakeSalt = Environment.GetEnvironmentVariable("MSBUILDNODEHANDSHAKESALT");
            var msBuildFile = new FileInfo(msBuildLocation);
            var msBuildDirectory = msBuildFile.DirectoryName;
            _salt = ComputeHandshakeHash(handshakeSalt + msBuildDirectory);
            Version fileVersion = new Version(FileVersionInfo.GetVersionInfo(msBuildLocation).FileVersion ?? string.Empty);
            _fileVersionMajor = fileVersion.Major;
            _fileVersionMinor = fileVersion.Minor;
            _fileVersionBuild = fileVersion.Build;
            _fileVersionRevision = fileVersion.Revision;
        }

        internal const int EndOfHandshakeSignal = -0x2a2a2a2a;

        /// <summary>
        /// Compute stable hash as integer
        /// </summary>
        private static int ComputeHandshakeHash(string fromString)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(fromString));

            return BitConverter.ToInt32(bytes, 0);
        }

        internal static int AvoidEndOfHandshakeSignal(int x)
        {
            return x == EndOfHandshakeSignal ? ~x : x;
        }

        public int[] RetrieveHandshakeComponents()
        {
            return new int[]
            {
                AvoidEndOfHandshakeSignal(_version),
                AvoidEndOfHandshakeSignal(_options),
                AvoidEndOfHandshakeSignal(_salt),
                AvoidEndOfHandshakeSignal(_fileVersionMajor),
                AvoidEndOfHandshakeSignal(_fileVersionMinor),
                AvoidEndOfHandshakeSignal(_fileVersionBuild),
                AvoidEndOfHandshakeSignal(_fileVersionRevision),
            };
        }

        public string GetKey()
        {
            return $"{_version} {_options} {_salt} {_fileVersionMajor} {_fileVersionMinor} {_fileVersionBuild} {_fileVersionRevision}"
                .ToString(CultureInfo.InvariantCulture);
        }

        public byte? ExpectedVersionInFirstByte => null;

        /// <summary>
        /// Computes Handshake stable hash string representing whole state of handshake.
        /// </summary>
        public string ComputeHash()
        {
            var input = GetKey();
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToBase64String(bytes)
                .Replace("/", "_")
                .Replace("=", string.Empty);
        }
    }

    internal class EntryNodeCommand
    {
        /// <summary>
        /// The startup directory
        /// </summary>
        private readonly string _commandLine;

        /// <summary>
        /// The startup directory
        /// </summary>
        private readonly string _startupDirectory;

        /// <summary>
        /// The process environment.
        /// </summary>
        private readonly IDictionary<string, string> _buildProcessEnvironment;

        /// <summary>
        /// The culture
        /// </summary>
        private readonly CultureInfo _culture;

        /// <summary>
        /// The UI culture.
        /// </summary>
        private readonly CultureInfo _uiCulture;

        public EntryNodeCommand(string commandLine, string startupDirectory, IDictionary<string, string> buildProcessEnvironment, CultureInfo culture, CultureInfo uiCulture)
        {
            _commandLine = commandLine;
            _startupDirectory = startupDirectory;
            _buildProcessEnvironment = buildProcessEnvironment;
            _culture = culture;
            _uiCulture = uiCulture;
        }

        /// <summary>
        /// Private constructor for deserialization
        /// </summary>
        private EntryNodeCommand()
        {
        }

        #region INodePacket Members

        /// <summary>
        /// Packet type.
        /// This has to be in sync with Microsoft.Build.BackEnd.NodePacketType.EntryNodeCommand
        /// </summary>
        public byte PacketType => 0xF0;

        #endregion

        /// <summary>
        /// The startup directory
        /// </summary>
        public string CommandLine => _commandLine;

        /// <summary>
        /// The startup directory
        /// </summary>
        public string StartupDirectory => _startupDirectory;

        /// <summary>
        /// The process environment.
        /// </summary>
        public IDictionary<string, string> BuildProcessEnvironment => _buildProcessEnvironment;

        /// <summary>
        /// The culture
        /// </summary>
        public CultureInfo CultureName => _culture;

        /// <summary>
        /// The UI culture.
        /// </summary>
        public CultureInfo UICulture => _uiCulture;

        public void WriteToStream(Stream outputStream)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // header
            bw.Write((byte)PacketType);
            bw.Write((int)0);
            int headerSize = (int)ms.Position;

            // body
            bw.Write(_commandLine);
            bw.Write(_startupDirectory);
            bw.Write((int)_buildProcessEnvironment.Count);
            foreach (var pair in _buildProcessEnvironment)
            {
                bw.Write(pair.Key);
                bw.Write(pair.Value);
            }
            bw.Write(_culture.Name);
            bw.Write(_uiCulture.Name);

            int bodySize = (int)ms.Position - headerSize;

            ms.Position = 1;
            ms.WriteByte((byte)bodySize);
            ms.WriteByte((byte)(bodySize >> 8));
            ms.WriteByte((byte)(bodySize >> 16));
            ms.WriteByte((byte)(bodySize >> 24));

            // copy packet message bytes into stream
            var bytes = ms.GetBuffer();
            outputStream.Write(bytes, 0, headerSize + bodySize);
        }
    }

    internal class EntryNodeResponse
    {
        private int _exitCode;
        private string _exitType;

        /// <summary>
        /// Private constructor for deserialization
        /// </summary>
        private EntryNodeResponse()
        {
        }

        #region INodePacket Members

        /// <summary>
        /// Packet type.
        /// This has to be in sync with Microsoft.Build.BackEnd.NodePacketType.EntryNodeResponse
        /// </summary>
        public const byte PacketType = 0xF1;

        #endregion

        public int ExitCode => _exitCode;

        public string ExitType => _exitType;

        public static EntryNodeResponse DeserializeFromStream(Stream inputStream)
        {
            EntryNodeResponse response = new();

            using var br = new BinaryReader(inputStream);

            response._exitCode = br.ReadInt32();
            response._exitType = br.ReadString();

            return response;
        }

    }

    internal class EntryNodeConsoleWrite
    {
        private string _text;
        private ConsoleColor _foreground;
        private ConsoleColor _background;
        private byte _outputType;

        public string Text => _text;

        public ConsoleColor Foreground => _foreground;

        public ConsoleColor Background => _background;

        /// <summary>
        /// 1 = stdout, 2 = stderr
        /// </summary>
        public byte OutputType => _outputType;

        private EntryNodeConsoleWrite()
        {
        }

        #region INodePacket Members

        /// <summary>
        /// Packet type.
        /// This has to be in sync with Microsoft.Build.BackEnd.NodePacketType.EntryNodeInfo
        /// </summary>
        public const byte PacketType = 0xF2;

        #endregion

        public static EntryNodeConsoleWrite DeserializeFromStream(Stream inputStream)
        {
            EntryNodeConsoleWrite consoleWrite = new();

            using var br = new BinaryReader(inputStream);

            consoleWrite._text = br.ReadString();
            consoleWrite._foreground = (ConsoleColor)br.ReadInt32();
            consoleWrite._background = (ConsoleColor)br.ReadInt32();
            consoleWrite._outputType = br.ReadByte();

            return consoleWrite;
        }
    }
}
