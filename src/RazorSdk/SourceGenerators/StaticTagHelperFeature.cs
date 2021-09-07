// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System.Collections.Generic;
using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators
{
    internal sealed class StaticTagHelperFeature : RazorEngineFeatureBase, ITagHelperFeature
    {
        public IReadOnlyList<TagHelperDescriptor> TagHelpers { get; set; }

        public IReadOnlyList<TagHelperDescriptor> GetDescriptors() => TagHelpers;

        public StaticTagHelperFeature()
        {
            TagHelpers = new List<TagHelperDescriptor>();
        }

        public StaticTagHelperFeature(IEnumerable<TagHelperDescriptor> tagHelpers)
        {
            TagHelpers = new List<TagHelperDescriptor>(tagHelpers);
        }
    }
}
