// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators
{
    internal class ConfigureRazorCodeGenerationOptions : RazorEngineFeatureBase, IConfigureRazorCodeGenerationOptionsFeature
    {
        private readonly Action<RazorCodeGenerationOptionsBuilder> _action;

        public ConfigureRazorCodeGenerationOptions(Action<RazorCodeGenerationOptionsBuilder> action)
        {
            _action = action;
        }

        public int Order { get; set; }

        public void Configure(RazorCodeGenerationOptionsBuilder options) => _action(options);
    }
}
