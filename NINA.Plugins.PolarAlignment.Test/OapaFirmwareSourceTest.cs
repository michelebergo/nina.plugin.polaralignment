using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The firmware source is an artifact we ship but never build here, so nothing in this
    /// solution would notice it becoming un-compilable. It reaches testers as a .ino inside
    /// a zip, gets unpacked by Windows and opened in the Arduino IDE - a path that mangles
    /// anything outside plain ASCII. A byte-order mark and a handful of em-dashes in the
    /// comments were enough to make it fail to compile for one tester while compiling fine
    /// for two others ("stray '\255' in program"). Keeping the file pure ASCII removes the
    /// whole class of environment-dependent failure.
    /// </summary>
    public class OapaFirmwareSourceTest {

        /// <summary>
        /// The firmware source is the beta line's working copy; its canonical home is the
        /// separate oapa-firmware repository, so a checkout without it is legitimate (an
        /// upstream PR branch, for one). The guard applies wherever the file exists - which
        /// includes every machine a release is ever packaged from.
        /// </summary>
        private static string FirmwarePath() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PolarAlignment", "NINA.Plugins.PolarAlignment.csproj"))) {
                dir = dir.Parent;
            }
            dir.Should().NotBeNull("the test must run inside the repository tree");
            var path = Path.Combine(dir.FullName, "firmware", "oapa.ino");
            if (!File.Exists(path)) {
                Assert.Ignore("firmware source not present in this checkout");
            }
            return path;
        }

        [Test]
        public void FirmwareSource_HasNoByteOrderMark() {
            var bytes = File.ReadAllBytes(FirmwarePath());

            bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF },
                "a UTF-8 BOM makes the Arduino toolchain report a stray character on line 1");
            bytes.Take(2).Should().NotEqual(new byte[] { 0xFF, 0xFE },
                "a UTF-16 BOM means the file was re-encoded and will not compile at all");
        }

        [Test]
        public void FirmwareSource_IsPureAscii() {
            var text = File.ReadAllText(FirmwarePath(), Encoding.UTF8);

            var offenders = text
                .Select((c, i) => (c, i))
                .Where(x => x.c > 127)
                .Select(x => $"U+{(int)x.c:X4} at offset {x.i}")
                .Distinct()
                .ToList();

            offenders.Should().BeEmpty("the .ino must survive being zipped, unzipped on Windows and opened in the Arduino IDE; use '-' instead of an em-dash and 'deg'/'^2' instead of symbols");
        }

        [Test]
        public void FirmwareSource_DeclaresTheVersionThePluginExpects() {
            var text = File.ReadAllText(FirmwarePath());

            // Guards against shipping a package whose firmware predates the features the
            // plugin now relies on (the F feed rate and the "!" stop are 1.2.1+).
            text.Should().Contain("#define FW_VERSION \"1.2.2\"");
            text.Should().Contain("jogSpeedFrom", "the jog feed rate must be honored");
            text.Should().Contain("xAxis.stepper.stop()", "the '!' stop command must be implemented");
        }
    }
}
