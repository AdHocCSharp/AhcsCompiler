namespace Ahcs.Expander;

public static class AdHocParserExtensions
{
    internal static (bool result, Commit? commit) ProcessToken(
        this CompilationUnitSyntax root,
        MSBuildWorkspace workspace,
        FileInfo fileInfo,
        SyntaxTrivia trivia,
        DirectoryInfo outputInfo,
        Signature identity,
        string defaultXml = AdHocParser.PROJECT_SDK_MICROSOFT_NET_SDK,
        ILogger? logger = null
    )
    {
        Repository? gitRepo = null;
        string logTemplate = $"[{MethodBase.GetCurrentMethod()?.Name}] {0}";
        SyntaxNode? structure = trivia.GetStructure();

        if (structure == null)
        {
            return (false, null);
        }

        root = root.RemoveNode(structure, SyntaxRemoveOptions.KeepNoTrivia) ?? root;

        string triviaText = trivia.GetProjectText();

        string? projectXml = GetProjectXml(triviaText, fileInfo);

        if (projectXml is null or "")
        {
            return (true, null);
        }

        string projectDirectoryPath = Path.Combine(
            outputInfo.FullName,
            fileInfo.Name.Replace(fileInfo.Extension, "", InvariantCultureIgnoreCase)
        );

        DirectoryInfo projectDirectory = new(projectDirectoryPath);

        logger?.LogInformation(
            logTemplate,
            $"[ProcessToken] Expanding Project Directory: {projectDirectory}"
        );

        string projectName = projectDirectory.Name;

        Project? oldProject
            = workspace.CurrentSolution.Projects.SingleOrDefault(p => p.Name == projectName);

        if (oldProject is not null)
        {
            logger?.LogInformation(
                logTemplate,
                $"[ProcessToken] Removing {projectName} from Solution"
            );

            workspace.TryApplyChanges(workspace.CurrentSolution.RemoveProject(oldProject.Id));
        }

        if (projectDirectory.Exists)
        {
            if (!Directory.Exists(Path.Combine(projectDirectoryPath, ".git")))
            {
                gitRepo = CreateRepository(projectDirectoryPath, identity);
            }
            else
            {
                gitRepo = new Repository(projectDirectoryPath);
                if (gitRepo.RetrieveStatus()
                    .IsDirty)
                {
                    gitRepo.Reset(ResetMode.Hard);
                }
            }
        }
        else
        {
            projectDirectory.Create();
            gitRepo = CreateRepository(projectDirectoryPath, identity);
        }

        string csprojFilename = Path.Combine(
            projectDirectory.FullName,
            fileInfo.Name.Replace(fileInfo.Extension, ".csproj", InvariantCultureIgnoreCase)
        );

        FileInfo projectFile = new(csprojFilename);

        using StreamWriter writer = new(projectFile.OpenWrite());

        writer.Write(projectXml);

        writer.Close();

        if (!projectFile.Exists)
        {
            return (false, null);
        }

        gitRepo.AddFile(projectFile);
        gitRepo.StageChanges(projectFile);
        var repoStatus1 = gitRepo.RetrieveStatus();


        logger?.LogInformation(
            logTemplate,
            "[ProcessToken] Created project file: {csprojFilename}"
        );

        root = root.CreateGlobalUsings(projectDirectory, gitRepo, logger);

        FileInfo newFile = root.CreateSourceFile(projectFile, fileInfo, gitRepo, logger);

        if (!newFile.Exists)
        {
            return (false, null);
        }

        logger?.LogInformation(logTemplate, $"[ProcessToken] Wrote Source to {newFile.FullName}");

        bool addResult = workspace.AddProjectToSolution(projectFile, defaultXml, logger);

        if (addResult)
        {
            return (true, null);
        }

        var readmeFilename = Path.Combine(projectDirectory.FullName, "README.md");
        File.WriteAllText(readmeFilename, "# Test Output");

        gitRepo.AddFile(readmeFilename);
        gitRepo.StageChanges(readmeFilename);

        var repoStatus = gitRepo.RetrieveStatus();
        if (!repoStatus.IsDirty)
        {
            return (true, gitRepo.Commits.First());
        }

        Commit? commit = gitRepo.CommitChanges(identity, "Created AdHoc Project");

        return (commit is not null, commit);

    }

    private static Repository CreateRepository(string path, Signature signature)
    {
        string? gitRepoPath = Repository.Init(path, false);
        Repository gitRepo = new Repository(gitRepoPath);

        gitRepo.Commit(
            "Initializing Repository",
            signature,
            signature,
            new CommitOptions
            {
                AllowEmptyCommit = true,
            }
        );

        return gitRepo;
    }

    public static bool AddProjectToSolution(
        this MSBuildWorkspace workspace,
        FileInfo projectFile,
        string projectXml = AdHocParser.PROJECT_SDK_MICROSOFT_NET_SDK,
        ILogger? logger = null
    )
    {
        string logTemplate = $"[{MethodBase.GetCurrentMethod()?.Name}] {0}";

        if (!projectFile.Exists)
        {
            using StreamWriter writer = projectFile.CreateText();
            writer.WriteLine(projectXml);
            writer.Flush();
            writer.Close();
        }

        Project project = workspace.OpenProjectAsync(projectFile.FullName)
            .GetAwaiter()
            .GetResult();

        logger?.LogInformation(
            logTemplate,
            $"[ProcessToken] Added new project to Solution: {project.FilePath}"
        );
        logger?.LogInformation(
            logTemplate,
            $"[ProcessToken] msbuildProject.AllEvaluatedItems.Count: {project.Documents.Count()}"
        );

        return false;
    }

    internal static FileInfo CreateSourceFile(
        this CompilationUnitSyntax root,
        FileInfo projectFile,
        FileInfo fileInfo,
        Repository gitRepo,
        ILogger? logger = null
    )
    {
        _ = projectFile.DirectoryName ??
            throw new ArgumentNullException(nameof(projectFile), "DirectoryName is null.");

        string logTemplate = $"[{MethodBase.GetCurrentMethod()?.Name}] {0}";

        string newFileName = Path.Combine(projectFile.DirectoryName, fileInfo.Name);
        string source = root.ToFullString();

        File.WriteAllText(newFileName, source);

        FileInfo newFile = new(newFileName);

        gitRepo.AddFile(newFile);
        gitRepo.StageChanges(newFile);
        var repoStatus2 = gitRepo.RetrieveStatus();

        logger?.LogDebug(logTemplate, $"Wrote {source.Length} characters to {fileInfo.Name}");

        return newFile;
    }

    internal static CompilationUnitSyntax CreateGlobalUsings(
        this CompilationUnitSyntax root,
        DirectoryInfo projectDirectory,
        Repository gitRepo,
        ILogger? logger = null
    )
    {
        string logTemplate = $"[{MethodBase.GetCurrentMethod()?.Name}] {0}";

        List<SyntaxNode> usingDirectives = root.GetUsingDirectives(logger);

        if (usingDirectives is not
            {
                Count: > 0,
            })
        {
            return root;
        }

        string globalUsingsFileName = Path.Combine(projectDirectory.FullName, "GlobalUsings.cs");

        string code = string.Join(
            Environment.NewLine,
            usingDirectives.Select(static d => $"global {d.ToFullString().Trim()}")
        );
        File.WriteAllText(globalUsingsFileName, code);

        gitRepo.AddFile(new FileInfo(globalUsingsFileName));
        gitRepo.StageChanges(new FileInfo(globalUsingsFileName));
        var repoStatus3 = gitRepo.RetrieveStatus();

        logger?.LogDebug(logTemplate, $"Created Global Usings at {globalUsingsFileName}");

        root = root.RemoveNodes(usingDirectives, SyntaxRemoveOptions.KeepNoTrivia) ?? root;

        return root;
    }

    internal static List<SyntaxNode> GetUsingDirectives(
        this CompilationUnitSyntax? root,
        ILogger? logger = null
    )
    {
        string logTemplate = $"[{MethodBase.GetCurrentMethod()?.Name}] {0}";

        List<SyntaxNode> usingDirectives = new();

        if (root is null)
        {
            return usingDirectives;
        }

        SyntaxToken directive = root.GetFirstToken(includeDirectives: true);

        if (directive.Parent is null)
        {
            directive = directive.GetNextToken(includeDirectives: true);
        }

        while ((directive.Parent?.Kind() ?? SyntaxKind.None) != SyntaxKind.None)
        {
            SyntaxKind? kind = directive.Parent?.Kind();

            logger?.LogInformation(logTemplate, $"kind: {kind}");

            bool found = kind switch
            {
                SyntaxKind.UsingDirective => true,
                _ => false,
            };

            if (found)
            {
                SyntaxNode? node = directive.Parent;
                string? code = node?.ToFullString();
                logger?.LogInformation(logTemplate, $"Using Directive: [{code}]");

                if (node != null)
                {
                    usingDirectives.Add(node);
                }
            }

            directive = directive.GetNextToken(includeDirectives: true);
        }

        return usingDirectives.Distinct()
            .ToList();
    }

    // ReSharper disable once SuggestBaseTypeForParameter

    internal static string? GetProjectXml(string trivia, FileInfo fileInfo, ILogger? logger = null)
    {
        string logTemplate = $"[{MethodBase.GetCurrentMethod()?.Name}] {0}";

        string? projectXml = null;

        try
        {
            XDocument xml = XDocument.Parse(trivia);

            if (xml.Root?.Name.LocalName is not "Project")
            {
                return null;
            }

            logger?.LogInformation(logTemplate, $"{fileInfo.Name} has valid Project xml.");

            projectXml = xml.ToString();
        }
        catch
        {
            // Ignore
        }

        projectXml ??= trivia.YamlToXml();

        return projectXml;
    }

    internal static string YamlToXml(
        this string yaml,
        string defaultXml = AdHocParser.PROJECT_SDK_MICROSOFT_NET_SDK
    )
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        object? project = deserializer.Deserialize(new StringReader(yaml));

        if (project is null or "")
        {
            return defaultXml;
        }

        ISerializer serializer = new SerializerBuilder().JsonCompatible()
            .Build();
        string json = serializer.Serialize(project);

        while (json.Contains("\"_", Ordinal))
        {
            json = json.Replace("\"_", "\"@", Ordinal);
        }

        XDocument? xml = JsonConvert.DeserializeXNode(json);

        return xml?.ToString() ?? defaultXml;
    }

    internal static string GetProjectText(this SyntaxTrivia trivia)
    {
        string triviaText = trivia.ToFullString();
        IEnumerable<string> lines = triviaText.Split('\n', StringSplitOptions.TrimEntries)
            .Select(static l => l.Replace("///", "", Ordinal));

        triviaText = string.Join(Environment.NewLine, lines);

        return triviaText;
    }

    internal static void AddFile(this Repository gitRepo, string fileName)
    {
        gitRepo.AddFile(new FileInfo(fileName));
    }

    internal static void AddFile(this Repository gitRepo, FileInfo fileInfo)
    {
        string? repoPath = gitRepo.Info.Path.Replace("\\", "/")
            .Replace("/.git/", "")
            .TrimEnd('/');
        string filename = fileInfo.FullName.Replace("\\", "/")
            .TrimEnd('/');

        if (!filename.StartsWith(repoPath, StringComparison.Ordinal))
        {
            throw new ArgumentException($"File is outside of the repository. [{repoPath}]");
        }

        filename = filename.Replace(repoPath, "").Trim('/');

        gitRepo.Index.Add(filename);

        gitRepo.Index.Write();
    }

    internal static void StageChanges(this Repository gitRepo, string fileName)
    {
        gitRepo.StageChanges(new FileInfo(fileName));
    }

    internal static void StageChanges(this Repository gitRepo, FileInfo fileInfo)
    {
        string? repoPath = gitRepo.Info.Path.Replace("\\", "/")
            .Replace("/.git/", "")
            .TrimEnd('/');
        string filename = fileInfo.FullName.Replace("\\", "/")
            .TrimEnd('/');

        if (!filename.StartsWith(repoPath, StringComparison.Ordinal))
        {
            throw new ArgumentException($"File is outside of the repository. [{repoPath}]");
        }

        filename = filename.Replace(repoPath, "")
            .Trim('/');

        Stage(gitRepo, filename);
    }

    internal static Commit? CommitChanges(this Repository gitRepo, Signature author, string message = "Commit Changes.")
        => gitRepo.Index.Count == 0
            ? null
            : gitRepo.Commit(message, author, author);
}
