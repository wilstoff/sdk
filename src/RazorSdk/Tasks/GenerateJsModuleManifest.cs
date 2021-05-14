// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.AspNetCore.Razor.Tasks
{
    public class GenerateJsModuleManifest : Task
    {
        private static readonly IComparer<ITaskItem> _importPathComparer =
            Comparer<ITaskItem>.Create((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.GetMetadata("ModuleImportPath"), y.GetMetadata("ModuleImportPath")));

        [Required]
        public ITaskItem[] JsModules { get; set; }

        [Required]
        public string OutputFile { get; set; }

        public override bool Execute()
        {
            Array.Sort(JsModules, _importPathComparer);
            var entries = new string[JsModules.Length];

            for (var i = 0; i < JsModules.Length; i++)
            {
                entries[i] = NormalizePath(JsModules[i].GetMetadata("ModuleImportPath"));
            }

            var content = Serialize(entries);
            if (!File.Exists(OutputFile) || !SameContent(content, OutputFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));
                File.WriteAllText(OutputFile, content);
            }

            return !Log.HasLoggedErrors;
        }

        private static string Serialize(string[] entries)
        {
            var serializer = new DataContractJsonSerializer(typeof(string[]), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });

            using var output = new MemoryStream();
            using var writer = JsonReaderWriterFactory
                .CreateJsonWriter(output, Encoding.UTF8, ownsStream: false, indent: true);

            serializer.WriteObject(writer, entries);
            writer.Flush();

            output.Seek(0, SeekOrigin.Begin);
            using var streamReader = new StreamReader(output);

            return streamReader.ReadToEnd().Replace("\\/","/");
        }

        private static string NormalizePath(string path) => path.StartsWith("./") ? path.Replace("\\", "/").Trim('/') : $"./{path.Replace("\\", "/").Trim('/')}";

        private static bool SameContent(string content, string outputFilePath)
        {
            var contentHash = GetContentHash(content);

            var outputContent = File.ReadAllText(outputFilePath);
            var outputContentHash = GetContentHash(outputContent);

            for (int i = 0; i < outputContentHash.Length; i++)
            {
                if (outputContentHash[i] != contentHash[i])
                {
                    return false;
                }
            }

            return true;

            static byte[] GetContentHash(string content)
            {
                using var sha256 = SHA256.Create();
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            }
        }
    }
}
