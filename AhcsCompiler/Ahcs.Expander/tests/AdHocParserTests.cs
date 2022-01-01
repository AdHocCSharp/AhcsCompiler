using Xunit;

using System;

namespace Ahcs.Expander.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using FluentAssertions;

    using LibGit2Sharp;

    using Microsoft.CodeAnalysis.MSBuild;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    using Xunit.Abstractions;

    using static StringComparison;

    public class AdHocParserTests
    {
        private readonly ITestOutputHelper _output;

        static AdHocParserTests()
        {
            const string SLUG = @"Ahcs.Expander";

            string pwd = Environment.CurrentDirectory;
            int index = pwd.IndexOf(SLUG, InvariantCultureIgnoreCase);

            if (index <= -1)
            {
                throw new ApplicationException($"Cannot locate test project folder. [{pwd}]");
            }

            index += SLUG.Length;

            string basePath = pwd[..index];

            string singleFilePath = Path.Combine(
                basePath,
                "TestFiles",
                "SingleFile"
            );

            string targetFilePath = Path.Combine(
                basePath,
                "OutputFiles",
                "SingleFile"
            );

            SourceFolder = new(singleFilePath);
            OutputFolder = new(targetFilePath);

            if (!OutputFolder.Exists)
            {
                OutputFolder.Create();
            }

            CleanFolder(OutputFolder);

            static void CleanFolder(DirectoryInfo folder)
            {
                if (!folder.Exists)
                {
                    throw new ArgumentException($"[{folder.FullName}] does not exist.");
                }

                FileInfo[] toClean = folder.GetFiles();

                foreach (FileInfo fileInfo in toClean)
                {
                    fileInfo.Delete();
                }
            }
        }

        public AdHocParserTests(ITestOutputHelper output)
            => _output = output;

        private static DirectoryInfo SourceFolder { get; }
        private static DirectoryInfo OutputFolder { get; }

        private static IEnumerable<object[]> GetFiles()
        {
            FileInfo[] files = SourceFolder.GetFiles("*.cs");
            foreach (FileInfo file in files)
            {
                yield return new object[]
                {
                    file,
                };
            }
        }

        [Theory, MemberData(nameof(GetFiles)),]
        public void ProcessFileTest(FileInfo sourceFile)
        {
            string filename = sourceFile.FullName;

            DirectoryInfo targetDirectory = new(
                Path.Combine(OutputFolder.FullName,
                    sourceFile.Name.Replace(".cs", "")));

            if (!targetDirectory.Exists)
            {
                targetDirectory.Create();
            }

            IHost host = BuildHost(targetDirectory);

            AdHocParser? parser = host.Services.GetService<AdHocParser>();

            parser.Should().NotBeNull();

            var (result, commit) =
                parser!.ProcessFile(filename, OutputFolder);

            result.Should().BeTrue();

            commit.Should()
                .NotBeNull();

            Repository.IsValid(targetDirectory.FullName)
                .Should()
                .BeTrue();

            using Repository gitRepo = new Repository(targetDirectory.FullName);

            gitRepo.Info.IsBare.Should()
                .BeFalse();

            gitRepo.Info.Path.Trim("\\/".ToCharArray()).Should()
                .Be(Path.Combine(targetDirectory.FullName, ".git"));

            gitRepo.Commits.Should()
                .NotBeNullOrEmpty();

            Commit? firstCommit = gitRepo.Commits.First();

            firstCommit.Should()
                .NotBeNull();

            firstCommit.Sha.Should()
                .Be(commit?.Sha);

            _output.WriteLine("\n## Commit Files\n");
            foreach (var treeItem in firstCommit.Tree)
            {
                _output.WriteLine($"- {treeItem.Name}");
            }

            FileInfo[] outputFiles = targetDirectory.GetFiles();

            outputFiles.Should()
                .NotBeNullOrEmpty();

            _output.WriteLine("\n## Output Files\n");

            string fileList = string.Join(
                Environment.NewLine,
                outputFiles.Select(static f => $"* {f.Name}"));

            _output.WriteLine(fileList);
        }

        private static IHost BuildHost(DirectoryInfo directory)
        {
            IHostBuilder? builder = Host.CreateDefaultBuilder();

            builder.ConfigureLogging(static loggingBuilder => loggingBuilder.AddConsole());

            builder.ConfigureServices(
                collection =>
                {
                    if (!directory.Exists)
                    {
                        directory.Create();
                    }

                    //DirectoryInfo gitDirectory = new(Path.Combine(directory.FullName, ".git"));

                    //if (!gitDirectory.Exists)
                    //{
                    //    Repository.Init(gitDirectory.FullName);
                    //}

                    //Repository repo = new(directory.FullName);

                    //collection.AddSingleton(repo);
                    collection.AddTransient(
                        static provider
                            => MSBuildWorkspace.Create(new Dictionary<string, string>())
                    );
                    collection.AddTransient<AdHocParser>();
                }
            );

            IHost? host = builder.Build();

            return host;
        }
    }
}