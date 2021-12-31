// ReSharper disable NotAccessedField.Local
// ReSharper disable UnusedMember.Local

[CheckBuildProjectConfigurations,ShutdownDotNetAfterServerBuild, ]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(static x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution? Solution;
    [GitRepository] readonly GitRepository? GitRepository;
    [GitVersion] readonly GitVersion? GitVersion;

    static AbsolutePath SourceDirectory => RootDirectory / "AhcCompiler";
    static AbsolutePath TestsDirectory => RootDirectory / "AhcCompiler";
    static AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(static () =>
        {
            Build.SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            Build.TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(Build.ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var result = DotNetBuild(s =>
            {
                s = s.SetProjectFile(Solution)
                    .SetConfiguration(Configuration)
                    .SetVerbosity(DotNetVerbosity.Minimal)
                    .SetNoLogo(true)
                    .EnableNoRestore();

                if (GitVersion is not null)
                {
                    s = s.SetAssemblyVersion(GitVersion.AssemblySemVer)
                        .SetFileVersion(GitVersion.AssemblySemFileVer)
                        .SetInformationalVersion(GitVersion.InformationalVersion);
                }

                return s;
            });

            var log = string.Join(Environment.NewLine, result.Select(o => o.Text));
            // Serilog.Log.Debug(log);

            if (log.IndexOf("Build succeeded.") is -1)
            {
                return;
            }

            string docfxConfig = RootDirectory + "/docfx_project/docfx.json";

            result = DocFXTasks.DocFXBuild(settings =>
                settings.SetConfigFile(docfxConfig)
                    .SetLogLevel(DocFXLogLevel.Warning)
            );
            // log = string.Join(Environment.NewLine, result.Select(o => o.Text));
            // Serilog.Log.Debug(log);
            Serilog.Log.Information($"Updated Docs with ExitCode: {ExitCode}");
        });

}
