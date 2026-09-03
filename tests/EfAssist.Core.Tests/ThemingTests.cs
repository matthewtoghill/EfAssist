using Avalonia.Media;
using EfAssist.App;
using EfAssist.App.ViewModels;
using EfAssist.Core;
using Xunit;

namespace EfAssist.Core.Tests;

public class ThemingTests
{
    [Fact]
    public void Every_preset_defines_both_variants()
    {
        foreach (var preset in ThemePresets.All)
        {
            foreach (var dark in new[] { false, true })
            {
                var palette = ThemePresets.Defaults(preset, dark);

                Assert.True(Color.TryParse(palette.Background, out _), $"{preset} {dark} background");
                Assert.True(Color.TryParse(palette.Accent, out _), $"{preset} {dark} accent");
                Assert.True(Color.TryParse(palette.Foreground, out _), $"{preset} {dark} foreground");
            }
        }
    }

    /// <summary>
    /// The whole point of the three-colour model is that text stays readable on the surface behind
    /// it. A preset that fails this ships an unusable theme.
    /// </summary>
    [Fact]
    public void Every_preset_keeps_text_readable_against_its_background()
    {
        foreach (var preset in ThemePresets.All)
        {
            foreach (var dark in new[] { false, true })
            {
                var palette = ThemePresets.Defaults(preset, dark);
                var background = Theming.Luminance(Color.Parse(palette.Background));
                var foreground = Theming.Luminance(Color.Parse(palette.Foreground));

                Assert.True(
                    System.Math.Abs(background - foreground) > 0.3,
                    $"{preset} {(dark ? "dark" : "light")} has too little contrast");
            }
        }
    }

    /// <summary>A light accent needs dark text on it, and vice versa.</summary>
    [Theory]
    [InlineData("#0078D4", 255)]
    [InlineData("#FFD700", 0)]
    [InlineData("#000000", 255)]
    [InlineData("#FFFFFF", 0)]
    public void Accent_text_contrasts_with_the_accent(string accent, byte expected)
    {
        var contrasting = Theming.Contrasting(Color.Parse(accent));

        Assert.Equal(expected, contrasting.R);
    }

    [Fact]
    public void Overrides_replace_only_the_slots_they_set()
    {
        var resolved = ThemePresets.Resolve(
            ThemePreset.Nord,
            dark: true,
            new ThemeColours { Accent = "#FF0000" });

        Assert.Equal("#FF0000", resolved.Accent);
        Assert.Equal(ThemePresets.Defaults(ThemePreset.Nord, true).Background, resolved.Background);
        Assert.Equal(ThemePresets.Defaults(ThemePreset.Nord, true).Foreground, resolved.Foreground);
    }

    [Theory]
    [InlineData(400, ThemePresets.MaxFontSize)]
    [InlineData(0, ThemePresets.MinFontSize)]
    [InlineData(-8, ThemePresets.MinFontSize)]
    [InlineData(16, 16)]
    public void Font_sizes_are_clamped_to_a_range_the_layout_survives(double given, double expected) =>
        Assert.Equal(expected, ThemePresets.ClampFontSize(given, ThemePresets.DefaultUiFontSize));

    [Fact]
    public void A_non_finite_font_size_falls_back_rather_than_clamping() =>
        Assert.Equal(
            ThemePresets.DefaultUiFontSize,
            ThemePresets.ClampFontSize(double.NaN, ThemePresets.DefaultUiFontSize));

    /// <summary>
    /// Setting a colour to the value the preset already uses must not record an override, or
    /// switching preset afterwards would leave that colour behind on the old palette.
    /// </summary>
    [Fact]
    public void Matching_the_preset_colour_by_hand_records_no_override()
    {
        var display = new DisplaySettings { Theme = AppTheme.Dark };
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });

        settings.Accent = Color.Parse(ThemePresets.Defaults(ThemePreset.Default, dark: true).Accent);

        Assert.True(display.DarkColours.IsDefault);
        Assert.False(settings.HasOverrides);
    }

    [Fact]
    public void Changing_a_colour_records_it_against_the_variant_being_edited()
    {
        var display = new DisplaySettings { Theme = AppTheme.Light };
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });

        settings.Background = Color.Parse("#123456");

        Assert.Equal("#123456", display.LightColours.Background);
        Assert.True(display.DarkColours.IsDefault);

        settings.EditingDark = true;
        settings.Background = Color.Parse("#654321");

        Assert.Equal("#654321", display.DarkColours.Background);
        Assert.Equal("#123456", display.LightColours.Background);
    }

    /// <summary>
    /// Loading the pickers for a newly chosen preset writes three colours into the view model. None
    /// of them may land in settings as an override, or the theme would be pinned to whatever preset
    /// was showing when the user last switched.
    /// </summary>
    [Fact]
    public void Switching_preset_keeps_untouched_colours_following_the_preset()
    {
        var display = new DisplaySettings { Theme = AppTheme.Dark };
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });

        settings.Preset = ThemePreset.Solarized;

        Assert.True(display.DarkColours.IsDefault);
        Assert.Equal(
            Color.Parse(ThemePresets.Defaults(ThemePreset.Solarized, dark: true).Background),
            settings.Background);
    }

    [Fact]
    public void Resetting_colours_clears_only_the_variant_on_screen()
    {
        var display = new DisplaySettings { Theme = AppTheme.Dark };
        display.LightColours.Accent = "#111111";
        display.DarkColours.Accent = "#222222";

        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });
        settings.ResetColoursCommand.Execute(null);

        Assert.True(display.DarkColours.IsDefault);
        Assert.Equal("#111111", display.LightColours.Accent);
    }

    [Fact]
    public void Choosing_an_explicit_variant_moves_the_pickers_to_it()
    {
        var display = new DisplaySettings { Theme = AppTheme.Light };
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });
        Assert.False(settings.EditingDark);

        settings.Theme = AppTheme.Dark;

        Assert.True(settings.EditingDark);
        Assert.Equal(AppTheme.Dark, display.Theme);
    }

    [Fact]
    public void Font_size_changes_reach_settings_clamped()
    {
        var display = new DisplaySettings();
        var saves = 0;
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => saves++);

        settings.UiFontSize = 999;
        settings.EditorFontSize = 18;

        Assert.Equal(ThemePresets.MaxFontSize, display.UiFontSize);
        Assert.Equal(18, display.EditorFontSize);
        Assert.Equal(2, saves);
    }

    [Fact]
    public void A_hand_edited_font_size_is_clamped_on_load()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"Display":{"UiFontSize":400,"EditorFontSize":-3}}""");

        var settings = SettingsStore.Load(path);

        Assert.Equal(ThemePresets.MaxFontSize, settings.Display.UiFontSize);
        Assert.Equal(ThemePresets.MinFontSize, settings.Display.EditorFontSize);
    }

    /// <summary>A settings file that says <c>null</c> for a block must not crash every reader.</summary>
    [Fact]
    public void A_null_settings_block_reads_as_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"Display":{"LightColours":null,"DarkColours":null}}""");

        var settings = SettingsStore.Load(path);

        Assert.True(settings.Display.LightColours.IsDefault);
        Assert.True(settings.Display.DarkColours.IsDefault);
    }

    /// <summary>
    /// A colour change is saved but cannot be repainted, so the settings screen has to say so — and has
    /// to stop saying so if the change is undone by hand.
    /// </summary>
    [Fact]
    public void A_colour_change_asks_for_a_restart_until_it_is_undone()
    {
        var display = new DisplaySettings { Theme = AppTheme.Dark };
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });
        Assert.False(settings.NeedsRestart);

        settings.Background = Color.Parse("#123456");
        Assert.True(settings.NeedsRestart);

        settings.Background = Color.Parse(ThemePresets.Defaults(ThemePreset.Default, dark: true).Background);
        Assert.False(settings.NeedsRestart);
    }

    [Fact]
    public void Switching_palette_asks_for_a_restart()
    {
        var display = new DisplaySettings();
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });

        settings.Preset = ThemePreset.Nord;

        Assert.True(settings.NeedsRestart);
    }

    /// <summary>
    /// The variant and the font sizes are applied to the running app, so neither is allowed to put the
    /// restart notice up.
    /// </summary>
    [Fact]
    public void The_variant_and_the_font_sizes_do_not_ask_for_a_restart()
    {
        var display = new DisplaySettings { Theme = AppTheme.Light };
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { });

        settings.Theme = AppTheme.Dark;
        settings.UiFontSize = 20;
        settings.EditorFontSize = 16;

        Assert.False(settings.NeedsRestart);
    }

    /// <summary>
    /// Editing one variant's colours must not ask for a restart on the strength of the other variant,
    /// but it must ask on the strength of its own — a System user can be flipped into either.
    /// </summary>
    [Fact]
    public void Editing_the_hidden_variant_still_asks_for_a_restart()
    {
        var display = new DisplaySettings { Theme = AppTheme.System };
        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { }) { EditingDark = false };
        Assert.False(settings.NeedsRestart);

        settings.EditingDark = true;
        Assert.False(settings.NeedsRestart);

        settings.Accent = Color.Parse("#ABCDEF");
        Assert.True(settings.NeedsRestart);
    }

    [Fact]
    public void The_preview_follows_the_variant_being_edited()
    {
        var display = new DisplaySettings { Theme = AppTheme.System };
        display.LightColours.Background = "#FFFFFF";
        display.DarkColours.Background = "#101010";

        var settings = new SettingsViewModel(new AppSettings { Display = display }, () => { }) { EditingDark = false };
        Assert.Equal(Colors.White, settings.Preview.Surface);

        settings.EditingDark = true;
        Assert.Equal(Color.Parse("#101010"), settings.Preview.Surface);
    }

    /// <summary>
    /// The preview is only honest if it derives from the same rules the real palette does, and if every
    /// value it produces is opaque — it is composited over the settings window, not over the surface it
    /// is describing.
    /// </summary>
    [Fact]
    public void The_preview_sample_is_opaque_and_derived_from_the_three_colours()
    {
        var palette = ThemePresets.Defaults(ThemePreset.Solarized, dark: true);
        var sample = Theming.Sample(palette, palette);

        Assert.Equal(Color.Parse(palette.Background), sample.Surface);
        Assert.Equal(Color.Parse(palette.Accent), sample.Accent);
        Assert.Equal(Color.Parse(palette.Foreground), sample.Text);
        Assert.Equal(Theming.Contrasting(Color.Parse(palette.Accent)), sample.OnAccent);

        foreach (var colour in new[]
                 {
                     sample.Surface, sample.Panel, sample.Border, sample.Accent, sample.OnAccent,
                     sample.Text, sample.MutedText,
                 })
        {
            Assert.Equal(255, colour.A);
        }
    }

    /// <summary>Muted text and panels have to sit between the surface and the text, not outside them.</summary>
    [Fact]
    public void The_preview_sample_keeps_its_derived_tones_between_surface_and_text()
    {
        foreach (var preset in ThemePresets.All)
        {
            foreach (var dark in new[] { false, true })
            {
                var palette = ThemePresets.Defaults(preset, dark);
                var sample = Theming.Sample(palette, palette);

                var surface = Theming.Luminance(sample.Surface);
                var text = Theming.Luminance(sample.Text);
                var low = Math.Min(surface, text);
                var high = Math.Max(surface, text);

                foreach (var tone in new[] { sample.Panel, sample.Border, sample.MutedText })
                {
                    var luminance = Theming.Luminance(tone);
                    Assert.InRange(luminance, low - 0.001, high + 0.001);
                }
            }
        }
    }

    /// <summary>An unparseable override falls back per slot rather than blanking the preview.</summary>
    [Fact]
    public void An_unparseable_override_falls_back_to_the_palette_value()
    {
        var fallback = ThemePresets.Defaults(ThemePreset.Nord, dark: true);
        var sample = Theming.Sample(fallback with { Accent = "not a colour" }, fallback);

        Assert.Equal(Color.Parse(fallback.Accent), sample.Accent);
    }
}
