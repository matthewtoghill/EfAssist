using EfAssist.Core;

namespace EfAssist.Core.Tests;

public class BundleFileNameTests
{
    [Fact]
    public void Puts_the_context_in_front_of_efbundle_so_two_contexts_do_not_collide() =>
        Assert.StartsWith("BlogContext-efbundle", BundleFileName.Suggest("BlogContext", "linux-x64"));

    [Fact]
    public void A_windows_runtime_gets_an_exe_extension() =>
        Assert.Equal("BlogContext-efbundle.exe", BundleFileName.Suggest("BlogContext", "win-arm64"));

    [Fact]
    public void A_non_windows_runtime_gets_no_extension_even_when_bundled_on_windows() =>
        Assert.Equal("BlogContext-efbundle", BundleFileName.Suggest("BlogContext", "linux-x64"));

    [Fact]
    public void An_unrecognised_runtime_is_treated_as_non_windows_rather_than_guessed_at() =>
        Assert.Equal("BlogContext-efbundle", BundleFileName.Suggest("BlogContext", "freebsd-x64"));

    [Fact]
    public void No_runtime_follows_the_machine_doing_the_bundling() =>
        Assert.Equal(
            OperatingSystem.IsWindows() ? "BlogContext-efbundle.exe" : "BlogContext-efbundle",
            BundleFileName.Suggest("BlogContext", null));

    [Fact]
    public void Falls_back_to_efs_own_name_when_there_is_no_context() =>
        Assert.Equal("efbundle", BundleFileName.Suggest(null, "linux-x64"));

    [Fact]
    public void A_separator_in_the_context_cannot_redirect_the_file() =>
        Assert.Equal("_etc_passwd-efbundle", BundleFileName.Suggest("/etc/passwd", "linux-x64"));
}
