// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.NET.TestFramework;
using Microsoft.NET.TestFramework.Assertions;
using Microsoft.NET.TestFramework.Commands;
using Microsoft.NET.TestFramework.Utilities;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.NET.Sdk.Razor.Tests
{
    public class ScopedJsIntegrationTests : AspNetSdkTest
    {
        public ScopedJsIntegrationTests(ITestOutputHelper log) : base(log) { }

        [Fact]
        public void Build_NoOps_WhenJsModulesIsDisabled()
        {
            var testAsset = "RazorComponentApp";
            var projectDirectory = CreateAspNetSdkTestAsset(testAsset);

            Directory.CreateDirectory(Path.Combine(projectDirectory.Path, "wwwroot"));
            File.WriteAllText(Path.Combine(projectDirectory.Path, "wwwroot", "RazorComponentApp.lib.module.js"), "export {}");

            var build = new BuildCommand(projectDirectory);
            build.Execute("/p:JsModulesEnabled=false").Should().Pass();

            var intermediateOutputPath = Path.Combine(build.GetBaseIntermediateDirectory().ToString(), "Debug", DefaultTfm);

            new FileInfo(Path.Combine(intermediateOutputPath, "jsmodules", "manifest", "ComponentApp.modules.json")).Should().NotExist();
        }

        [Fact]
        public void Build_GeneratesManifest_WhenJsModulesIsEnabled()
        {
            var expectedContents = @"[
  ""./RazorComponentApp.lib.module.js""
]";
            var testAsset = "RazorComponentApp";
            var projectDirectory = CreateAspNetSdkTestAsset(testAsset);

            Directory.CreateDirectory(Path.Combine(projectDirectory.Path, "wwwroot"));
            File.WriteAllText(Path.Combine(projectDirectory.Path, "wwwroot", "RazorComponentApp.lib.module.js"), "export {}");

            var build = new BuildCommand(projectDirectory);
            build.Execute().Should().Pass();

            var intermediateOutputPath = Path.Combine(build.GetBaseIntermediateDirectory().ToString(), "Debug", DefaultTfm);

            string manifest = Path.Combine(intermediateOutputPath, "jsmodules", "manifest", "ComponentApp.modules.json");
            new FileInfo(manifest).Should().Exist();

            var manifestContents = File.ReadAllText(manifest);
            manifestContents.Should().Be(expectedContents);
        }

        [Fact]
        public void Publish_NoBuild_PublishesJsModuleManifestToTheRightLocation()
        {
            var testAsset = "RazorComponentApp";
            var projectDirectory = CreateAspNetSdkTestAsset(testAsset);

            Directory.CreateDirectory(Path.Combine(projectDirectory.Path, "wwwroot"));
            File.WriteAllText(Path.Combine(projectDirectory.Path, "wwwroot", "RazorComponentApp.lib.module.js"), "export {}");

            var build = new BuildCommand(projectDirectory);
            build.Execute().Should().Pass();

            var publish = new PublishCommand(Log, projectDirectory.TestRoot);
            publish.Execute("/p:NoBuild=true").Should().Pass();

            var publishOutputPath = publish.GetOutputDirectory(DefaultTfm, "Debug").ToString();

            new FileInfo(Path.Combine(publishOutputPath, "wwwroot", "RazorComponentApp.lib.module.js")).Should().Exist();
            new FileInfo(Path.Combine(publishOutputPath, "wwwroot", "_content", "ComponentApp", "ComponentApp.modules.json")).Should().Exist();
        }

        [Fact]
        public void Publish_PublishesJsModuleManifestToTheRightLocation()
        {
            var testAsset = "RazorComponentApp";
            var projectDirectory = CreateAspNetSdkTestAsset(testAsset);

            Directory.CreateDirectory(Path.Combine(projectDirectory.Path, "wwwroot"));
            File.WriteAllText(Path.Combine(projectDirectory.Path, "wwwroot", "RazorComponentApp.lib.module.js"), "export {}");

            var build = new BuildCommand(projectDirectory);
            build.Execute().Should().Pass();

            var publish = new PublishCommand(Log, projectDirectory.TestRoot);
            publish.Execute().Should().Pass();

            var publishOutputPath = publish.GetOutputDirectory(DefaultTfm, "Debug").ToString();

            new FileInfo(Path.Combine(publishOutputPath, "wwwroot", "RazorComponentApp.lib.module.js")).Should().Exist();
            new FileInfo(Path.Combine(publishOutputPath, "wwwroot", "_content", "ComponentApp", "ComponentApp.modules.json")).Should().Exist();
        }

        [Fact]
        public void Publish_DoesNotPublishAnyFile_WhenThereAreNoJsModules()
        {
            var testAsset = "RazorComponentApp";
            var projectDirectory = CreateAspNetSdkTestAsset(testAsset);

            var publish = new PublishCommand(Log, projectDirectory.TestRoot);
            publish.Execute("/bl").Should().Pass();

            var publishOutputPath = publish.GetOutputDirectory(DefaultTfm, "Debug").ToString();

            new FileInfo(Path.Combine(publishOutputPath, "wwwroot", "_content", "ComponentApp", "ComponentApp.modules.json")).Should().NotExist();
        }
    }
}
