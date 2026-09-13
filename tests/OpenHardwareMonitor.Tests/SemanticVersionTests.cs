/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using OpenHardwareMonitor.Hardware.Maintenance;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  public class SemanticVersionTests {

    [Theory]
    [InlineData("v0.11.0", 0, 11, 0, "")]
    [InlineData("0.10.0-dev+852d066", 0, 10, 0, "dev")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("V2.0.0-rc.1", 2, 0, 0, "rc.1")]
    [InlineData(" 3.4.5 ", 3, 4, 5, "")]
    public void ParsesTagsAndInformationalVersions(string text, int major, int minor,
      int patch, string preRelease) {
      Assert.True(SemanticVersion.TryParse(text, out SemanticVersion? version));
      Assert.Equal(major, version!.Major);
      Assert.Equal(minor, version.Minor);
      Assert.Equal(patch, version.Patch);
      Assert.Equal(preRelease, string.Join(".", version.PreRelease));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-beta_1")]
    [InlineData("1234567890.0.0")]
    [InlineData("latest")]
    public void RejectsWhatIsNotAVersion(string text) {
      Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void ComparesNumbersNumerically() {
      Assert.True(SemanticVersion.Parse("0.10.0") > SemanticVersion.Parse("0.9.0"));
      Assert.True(SemanticVersion.Parse("0.9.10") > SemanticVersion.Parse("0.9.9"));
      Assert.True(SemanticVersion.Parse("1.0.0") > SemanticVersion.Parse("0.99.99"));
    }

    [Fact]
    public void FollowsSemVerPrecedence() {
      // The example from semver.org, section 11.
      string[] ordered = {
        "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
        "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"
      };
      for (int i = 1; i < ordered.Length; i++) {
        SemanticVersion lower = SemanticVersion.Parse(ordered[i - 1]);
        SemanticVersion higher = SemanticVersion.Parse(ordered[i]);
        Assert.True(lower < higher, ordered[i - 1] + " < " + ordered[i]);
        Assert.True(higher > lower);
      }
    }

    [Fact]
    public void ComparesLongNumericIdentifiersWithoutOverflow() {
      Assert.True(SemanticVersion.Parse("1.0.0-12345678901234567890") >
        SemanticVersion.Parse("1.0.0-9"));
    }

    [Fact]
    public void IgnoresBuildMetadata() {
      SemanticVersion a = SemanticVersion.Parse("0.10.0-dev+852d066");
      SemanticVersion b = SemanticVersion.Parse("0.10.0-dev+aaaaaaa");
      Assert.Equal(a, b);
      Assert.Equal(a.GetHashCode(), b.GetHashCode());
      Assert.Equal("852d066", a.BuildMetadata);
      Assert.Equal("0.10.0-dev", a.ToString());
    }

    [Fact]
    public void RecognisesDevelopmentBuilds() {
      Assert.True(SemanticVersion.Parse("0.10.0-dev").IsDevelopmentBuild);
      Assert.True(SemanticVersion.Parse("0.10.0-dev+852d066").IsDevelopmentBuild);
      Assert.False(SemanticVersion.Parse("0.10.0-beta.1").IsDevelopmentBuild);
      Assert.False(SemanticVersion.Parse("0.10.0").IsDevelopmentBuild);
      Assert.Equal("0.10.0", SemanticVersion.Parse("0.10.0-dev").Core.ToString());
    }
  }
}
