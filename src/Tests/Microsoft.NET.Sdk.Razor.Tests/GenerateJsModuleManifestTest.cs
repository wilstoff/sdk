// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.AspNetCore.Razor.Tasks;
using Microsoft.NET.TestFramework.Assertions;
using Xunit;

namespace Microsoft.NET.Sdk.Razor.Test
{
    public class GenerateJsModuleManifestTest
    {
        private readonly string ManifestContent = "[\r\n  \"./_content/PackageLib/Package.lib.module.js\",\r\n  \"./_content/ProjectLib/index.lib.module.js\"\r\n]";

        [Fact]
        public void Generates_EmptyManifest_NoJsModules()
        {
            // Arrange
            var expectedFile = Path.Combine(Directory.GetCurrentDirectory(), $"{Guid.NewGuid():N}.json");
            var taskInstance = new GenerateJsModuleManifest()
            {
                JsModules = Array.Empty<ITaskItem>(),
                OutputFile = expectedFile
            };

            // Act
            var result = taskInstance.Execute();

            // Assert
            result.Should().BeTrue();
            File.Exists(expectedFile).Should().BeTrue();
            File.ReadAllText(expectedFile).Should().Be("[ ]");
        }

        [Fact]
        public void Generates_Manifest_WhenJsModules()
        {
            // Arrange
            var expectedFile = Path.Combine(Directory.GetCurrentDirectory(), $"{Guid.NewGuid():N}.css");
            var taskInstance = new GenerateJsModuleManifest()
            {
                JsModules = new[]
                {
                    new TaskItem(
                        "TestFiles/Generated/ProjectLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/ProjectLib/index.lib.module.js"
                        }),
                    new TaskItem(
                        "TestFiles/Generated/PackageLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/PackageLib/Package.lib.module.js"
                        }),
                },
                OutputFile = expectedFile
            };

            // Act
            var result = taskInstance.Execute();

            // Assert
            result.Should().BeTrue();
            File.Exists(expectedFile).Should().BeTrue();

            var actualContents = File.ReadAllText(expectedFile);
            actualContents.Should().Be(ManifestContent);
        }

        [Fact]
        public void Generates_Manifest_DoesNotDependOnOrder()
        {
            // Arrange
            var expectedFile = Path.Combine(Directory.GetCurrentDirectory(), $"{Guid.NewGuid():N}.css");
            var taskInstance = new GenerateJsModuleManifest()
            {
                JsModules = new[]
                {
                    new TaskItem(
                        "TestFiles/Generated/PackageLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/PackageLib/Package.lib.module.js"
                        }),
                    new TaskItem(
                        "TestFiles/Generated/ProjectLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/ProjectLib/index.lib.module.js"
                        }),
                },
                OutputFile = expectedFile
            };

            // Act
            var result = taskInstance.Execute();

            // Assert
            result.Should().BeTrue();
            File.Exists(expectedFile).Should().BeTrue();

            var actualContents = File.ReadAllText(expectedFile);
            actualContents.Should().Be(ManifestContent);
        }

        [Fact]
        public void Generates_Manifest_DoesNotOverrideFile_WhenSameContents()
        {
            // Arrange
            var expectedFile = Path.Combine(Directory.GetCurrentDirectory(), $"{Guid.NewGuid():N}.css");
            var taskInstance = new GenerateJsModuleManifest()
            {
                JsModules = new[]
                {
                    new TaskItem(
                        "TestFiles/Generated/PackageLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/PackageLib/Package.lib.module.js"
                        }),
                    new TaskItem(
                        "TestFiles/Generated/ProjectLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/ProjectLib/index.lib.module.js"
                        }),
                },
                OutputFile = expectedFile
            };

            var result = taskInstance.Execute();
            result.Should().BeTrue();
            var originalTimeStamp = File.GetLastWriteTimeUtc(expectedFile);

            // Act
            var newResult = taskInstance.Execute();
            newResult.Should().BeTrue();

            // Assert
            originalTimeStamp.Should().BeSameDateAs(File.GetLastWriteTimeUtc(expectedFile));

            var actualContents = File.ReadAllText(expectedFile);
            actualContents.Should().Be(ManifestContent);
        }

        [Fact]
        public void Generates_Manifest_OverridesFile_WhenContentsChange()
        {
            // Arrange
            var expectedFile = Path.Combine(Directory.GetCurrentDirectory(), $"{Guid.NewGuid():N}.css");
            var taskInstance = new GenerateJsModuleManifest()
            {
                JsModules = new[]
                {
                    new TaskItem(
                        "TestFiles/Generated/PackageLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/PackageLib/Package.lib.module.js"
                        }),
                    new TaskItem(
                        "TestFiles/Generated/ProjectLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "_content/ProjectLib/index.lib.module.js"
                        }),
                },
                OutputFile = expectedFile
            };

            var result = taskInstance.Execute();
            result.Should().BeTrue();
            var originalTimeStamp = File.GetLastWriteTimeUtc(expectedFile);

            taskInstance.JsModules = new ITaskItem[0];

            // Act
            var newResult = taskInstance.Execute();
            newResult.Should().BeTrue();

            // Assert
            originalTimeStamp.Should().BeBefore(File.GetLastWriteTimeUtc(expectedFile));

            var actualContents = File.ReadAllText(expectedFile);
            actualContents.Should().Be("[ ]");
        }

        [Fact]
        public void Generate_NormalizesManifestPaths()
        {
            // Arrange
            var expectedFile = Path.Combine(Directory.GetCurrentDirectory(), $"{Guid.NewGuid():N}.css");
            var taskInstance = new GenerateJsModuleManifest()
            {
                JsModules = new[]
                {
                    new TaskItem(
                        "TestFiles/Generated/PackageLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "./_content\\PackageLib\\Package.lib.module.js"
                        }),
                    new TaskItem(
                        "TestFiles/Generated/ProjectLib.lib.module.js",
                        new Dictionary<string,string>
                        {
                            ["ModuleImportPath"] = "\\_content/ProjectLib/index.lib.module.js"
                        }),
                },
                OutputFile = expectedFile
            };

            // Act
            var result = taskInstance.Execute();

            // Assert
            result.Should().BeTrue();
            File.Exists(expectedFile).Should().BeTrue();

            var actualContents = File.ReadAllText(expectedFile);
            actualContents.Should().Be(ManifestContent);
        }
    }
}
