using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// XAML is invisible to the compiler: a mistyped binding fails silently at runtime, and
    /// a missing field (the rc10 azimuth-backlash asymmetry) fails no build at all. These
    /// tests parse the panel sources and pin three contracts: every binding resolves to a
    /// real VM member, the two motor panels are exact X/Y mirrors of each other, and the
    /// azimuth backlash option is labeled in arcminutes (the D1 fix, once lost to a branch
    /// gap and invisible until a tester asked).
    /// </summary>
    public class OapaXamlContractTest {

        private static string RepoFile(string relative) {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PolarAlignment", "NINA.Plugins.PolarAlignment.csproj"))) {
                dir = dir.Parent;
            }
            dir.Should().NotBeNull("the test must run inside the repository tree");
            return Path.Combine(dir.FullName, "PolarAlignment", relative);
        }

        private static string PanelXaml => File.ReadAllText(RepoFile(Path.Combine("OAPA", "OAPAControlPanel.xaml")));

        private static IEnumerable<string> BindingPaths(string xaml) =>
            Regex.Matches(xaml, @"\{Binding\s+([A-Za-z_][A-Za-z0-9_.]*)")
                .Select(m => m.Groups[1].Value)
                .Distinct();

        [Test]
        public void EveryPanelBinding_ResolvesToAPublicVmMember() {
            var vm = typeof(UniversalPolarAlignmentOAPAVM);
            var unresolved = BindingPaths(PanelXaml)
                .Where(p => !p.Contains('.')) // sub-paths bind through other objects
                .Where(p => vm.GetProperty(p, BindingFlags.Public | BindingFlags.Instance) == null)
                .ToList();

            unresolved.Should().BeEmpty("every {Binding} in the OAPA panel must match a public VM property - a typo here fails silently at runtime");
        }

        [Test]
        public void MotorPanels_AreExactAxisMirrors() {
            // Cut the two GroupBox sections out of the panel source.
            var xaml = PanelXaml;
            string Section(string header) {
                var start = xaml.IndexOf($"Header=\"{header}\"", StringComparison.Ordinal);
                start.Should().BeGreaterThan(0, $"the panel must contain the '{header}' group");
                var end = xaml.IndexOf("</GroupBox>", start, StringComparison.Ordinal);
                return xaml.Substring(start, end - start);
            }

            var azimuth = BindingPaths(Section("Azimuth Motor Settings")).ToHashSet();
            var altitude = BindingPaths(Section("Altitude Motor Settings")).ToHashSet();

            // Map Y-axis names onto X-axis names and require identical sets: whatever one
            // axis exposes, the other must expose too. This is the test that would have
            // caught the rc10 asymmetry (azimuth had no backlash value field).
            var altitudeMappedToAzimuth = altitude
                .Select(p => p.StartsWith("Y") ? "X" + p.Substring(1) : p)
                .ToHashSet();

            azimuth.Should().BeEquivalentTo(altitudeMappedToAzimuth,
                "the azimuth and altitude motor panels must expose the same controls for their own axis");
        }

        [Test]
        public void EveryStaticResourceReference_IsDeclaredInThePanel() {
            var xaml = PanelXaml;
            var declared = Regex.Matches(xaml, @"x:Key=""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
            var referenced = Regex.Matches(xaml, @"\{StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)\}").Select(m => m.Groups[1].Value).Distinct();

            // Converters and other keys come from merged dictionaries; only the panel's own
            // tooltip strings are checked here - a missing one throws XamlParseException and
            // takes the whole dock down at runtime.
            var missingTooltips = referenced.Where(r => r.EndsWith("ToolTip") && !declared.Contains(r)).ToList();

            missingTooltips.Should().BeEmpty("a {StaticResource} without a matching x:Key crashes the panel when it is opened");
        }

        [Test]
        public void AzimuthBacklashOption_IsShownOnlyForSystemsWithoutTheirOwnPanel() {
            // The value is one setting with two views, which reads as a duplicate to OAPA
            // users now that the motor panel owns it. UPAS has no motor panel, so the
            // options row stays for it - and only for it.
            var options = XDocument.Parse(File.ReadAllText(RepoFile("Options.xaml")));
            var azBacklashRow = options.Descendants()
                .Where(e => ((string)e.Attribute("ToolTip")) == "{StaticResource AzimuthBacklashCompensationToolTip}")
                .ToList();

            azBacklashRow.Should().NotBeEmpty("the options page must still expose the azimuth backlash for UPAS");
            azBacklashRow.Should().OnlyContain(e => ((string)e.Attribute("Visibility")).Contains("IsUPASSelected"),
                "with OAPA selected the value belongs in the motor panel only");
        }

        [Test]
        public void AzimuthBacklashOption_IsLabeledInArcminutes() {
            var options = XDocument.Parse(File.ReadAllText(RepoFile("Options.xaml")));
            var azBacklashBox = options.Descendants()
                .FirstOrDefault(e => (string)e.Attribute("Text") == "{Binding ActiveSystem.XBacklashCompensation}");

            azBacklashBox.Should().NotBeNull("the options page must expose the azimuth backlash compensation");
            ((string)azBacklashBox.Attribute("Unit")).Should().Be("arcmin",
                "the azimuth backlash value is in arcminutes; the 'steps' label was the D1 bug");
        }
    }
}
