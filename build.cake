#tool "nuget:?package=NuGet.CommandLine&version=5.5.1"
#tool "nuget:?package=GitVersion.CommandLine&version=5.3.5"
#tool "nuget:?package=OpenCover&version=4.7.922"
#addin "nuget:?package=Cake.Curl&version=4.1.0"
#addin "nuget:?package=Cake.Git&version=1.0.1"
#addin "nuget:?package=Cake.FileHelpers&version=3.0.0"
#tool "nuget:?package=Microsoft.TestPlatform&version=16.6.1"
#addin "nuget:?package=Newtonsoft.Json&version=9.0.1&prerelease"
#tool "nuget:?package=coverlet.console&version=3.1.2"
#tool "nuget:?package=Microsoft.CodeCoverage&version=16.9.4"
using Cake.Common.Tools.GitVersion;
using Newtonsoft.Json; 
using System.Net.Http;
using System.Threading;
using Cake.Core.Text;

public const string PROVIDED_BY_GITHUB = "PROVIDED_BY_GITHUB";

var solution = Argument("solution", "./GitSemVersioning.sln");
var target = Argument("do", "build");
var configuration = Argument("configuration", "Release");
var testResultsDir = Directory("./TestResults");
var buildVersion = "1.1";
var ouputDir = Directory("./obj");

// Removed sonarQube and artifactory arguments

var gitVersion = GitVersion(new GitVersionSettings {});
var githubBuildNumber = gitVersion.CommitsSinceVersionSource;
var gitProjectVersionNumber = gitVersion.MajorMinorPatch;
public string completeVersionForAssemblyInfo = string.Concat(gitProjectVersionNumber,".",githubBuildNumber);
public string completeVersionForWix = string.Concat(gitProjectVersionNumber,".",githubBuildNumber);

var gitUserName = Argument("gitusername", "PROVIDED_BY_GITHUB"); 
var gitUserPassword = Argument("gituserpassword", "PROVIDED_BY_GITHUB"); 

// Removed artifactory repo variables
var zipPath = new DirectoryPath("./artifact");

var EXG401UIAssemblyVersion = completeVersionForWix;

Task("Clean").Does(() => {
	CleanDirectories("./artifact");
    CleanDirectories("./TestResults");
	CleanDirectories("**/bin/" + configuration);
	CleanDirectories("**/obj/" + configuration);
});

Task("Restore")
    .Does(() => {
        DotNetRestore("./GitSemVersioning.sln");
    });

Task("Build").IsDependentOn("Restore").Does(() => 
{
    DotNetBuild("./GitSemVersioning.sln", new DotNetBuildSettings
    {
        Configuration = configuration,
		OutputDirectory = ouputDir
    });

});

// Note: ContinueOnError for test Task to allow Bamboo capture TestResults produced and halt pipeline from there.

Task("Test").ContinueOnError().Does(() =>
{
    var testProjects = GetFiles("./**/*.Test.csproj");
    foreach (var project in testProjects)
    {
        var projectName = project.GetFilenameWithoutExtension();
        var testSettings = new DotNetTestSettings
        {
            Loggers = new[] { $"trx;LogFileName={projectName}.trx" },
            ArgumentCustomization = args => args
                .Append("--collect:\"XPlat Code Coverage\"")
                .Append("/p:CollectCoverage=true")
                .Append("-- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover")
        };
        DotNetTest(project.FullPath, testSettings);
    }

    // Copy Test Results and Coverage Reports
    var testResultsDir = Directory("./TestResults");
    var coverageResultsDir = Directory("./CoverageResults");
    EnsureDirectoryExists(testResultsDir);
    EnsureDirectoryExists(coverageResultsDir);

    var trxFiles = GetFiles("./**/*.trx");
    foreach (var file in trxFiles)
    {
        CopyFileToDirectory(file, testResultsDir);
    }

    var coverageFiles = GetFiles("./**/coverage*.xml");
    foreach (var file in coverageFiles)
    {
        CopyFileToDirectory(file, coverageResultsDir);
    }

    Information("Test Results:");
    foreach (var file in GetFiles(testResultsDir.Path.FullPath + "/*.trx"))
    {
        Information(file.FullPath);
    }

    Information("Coverage Results:");
    foreach (var file in GetFiles(coverageResultsDir.Path.FullPath + "/*.xml"))
    {
        Information(file.FullPath);
    }

});

Task("Tagmaster").Does(() => {
    //Sanity check
    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if(!isGitHubActions)
    {
        Information("Task is not running by automation pipeline, skip.");
        return;
    }
    Information("Task is running by automation pipeline.");
    Information("Running inside GitHub Actions.");
    Information("GitVersion details: {0}", JsonConvert.SerializeObject(gitVersion, Formatting.Indented));

    //List and check existing tags
    Information("Version (401 BL Application): {0}", completeVersionForWix); 
    Information("BranchName: {0}", gitVersion.BranchName);
    Information("Previous Releases:");
    var currentTags = GitTags(".");
    foreach(var tag in currentTags)
    {
        Information(tag.FriendlyName);
    }
    //comment below line to consider all branches
    if (gitVersion.BranchName != "master" && gitVersion.BranchName != "develop" && !gitVersion.BranchName.StartsWith("release/") && !gitVersion.BranchName.StartsWith("hotfix/"))
    {
        Information($"Current branch '{gitVersion.BranchName}' is not master/develop/releaes/hotfix. Skipping tagging.");
        return;
    }
    if(string.IsNullOrEmpty(gitUserName) || string.IsNullOrEmpty(gitUserPassword) ||
        gitUserName == "PROVIDED_BY_GITHUB" || gitUserPassword == "PROVIDED_BY_GITHUB")
    {
        throw new Exception("Git Username/Password not provided to automation script.");
    }

    string branchTag;
    if (gitVersion.BranchName == "master")
    {
        branchTag = $"v{gitVersion.MajorMinorPatch}.{gitVersion.CommitsSinceVersionSource}";
    }
    else if (
        gitVersion.BranchName == "develop" ||
        gitVersion.BranchName.StartsWith("release/") ||
        gitVersion.BranchName.StartsWith("hotfix/")
    )
    {
        branchTag = $"v{gitVersion.MajorMinorPatch}-{gitVersion.PreReleaseLabel}.{gitVersion.CommitsSinceVersionSource}";
    }
    else
    {
        throw new Exception($"Branch '{gitVersion.BranchName}' is not supported for tagging.");
    }
    
    if(currentTags.Any(t => t.FriendlyName == branchTag))
    {
        Information($"Tag {branchTag} already exists, skip tagging.");
        return;
    }
    //Tag locally
    var workingDir = MakeAbsolute(Directory("./"));
    Information($"Tagging branch as: {branchTag} in resolved working dir: {workingDir}");
    GitTag(workingDir, branchTag);
    //Push tag to origin
    Information($"Pushing Tag to origin");
    
    string remoteUrl;
    int exitCode = StartProcess("git", new ProcessSettings{
        Arguments = "config --get remote.origin.url",
        RedirectStandardOutput = true
    }, out IEnumerable<string> remoteUrlOutput);

    if(exitCode != 0 || !remoteUrlOutput.Any()){
        throw new Exception("Could not get remote origin URL");
    }
    remoteUrl = remoteUrlOutput.First();

    var authenticatedUrl = remoteUrl.Replace("https://", $"https://{gitUserName}:{gitUserPassword}@");

    var pushArguments = new ProcessArgumentBuilder()
            .Append("push")
            .AppendQuoted(authenticatedUrl)
            .Append(branchTag);

    var processSettings = new ProcessSettings
    {
        Arguments = pushArguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        NoWindow = true
    };
    
    Information($"Executing: git {processSettings.Arguments.Render()}");

    string stdOut = "";
    string stdErr = "";
    
    exitCode = StartProcess("git", processSettings, out var redirectedStandardOutput, out var redirectedStandardError);

    stdOut = string.Join(Environment.NewLine, redirectedStandardOutput);
    stdErr = string.Join(Environment.NewLine, redirectedStandardError);

    if (exitCode != 0)
    {
        Error("Failed to push tag to origin.");
        Error("StdOut: " + stdOut);
        Error("StdErr: " + stdErr);
        throw new Exception("Failed to push tag.");
    }
    else
    {
        Information("Tag successfully pushed to origin.");
        Information("StdOut: " + stdOut);
    }
});


Task("full")
    .IsDependentOn("Clean")
    .IsDependentOn("Build")
    .IsDependentOn("Test")
    .IsDependentOn("Tagmaster");

RunTarget(target);