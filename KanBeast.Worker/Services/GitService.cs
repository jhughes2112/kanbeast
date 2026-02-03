using System.Diagnostics;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services;

public interface IGitService
{
    Task<string> CloneRepositoryAsync(string repoUrl, string workDir);
    Task<string> CreateOrCheckoutBranchAsync(string branchName, string workDir);
    Task ConfigureGitAsync(string username, string email, string workDir);
    Task CommitChangesAsync(string message, string workDir);
    Task PushChangesAsync(string workDir);
    Task RebaseToMasterAsync(string workDir);
}

public class GitService : IGitService
{
    public async Task<string> CloneRepositoryAsync(string repoUrl, string workDir)
    {
        var result = await ExecuteGitCommandAsync($"clone {repoUrl} {workDir}", Directory.GetCurrentDirectory());
        return result;
    }

    public async Task<string> CreateOrCheckoutBranchAsync(string branchName, string workDir)
    {
        // Check if branch exists
        var branches = await ExecuteGitCommandAsync("branch -a", workDir);
        
        if (branches.Contains(branchName))
        {
            return await ExecuteGitCommandAsync($"checkout {branchName}", workDir);
        }
        else
        {
            return await ExecuteGitCommandAsync($"checkout -b {branchName}", workDir);
        }
    }

    public async Task ConfigureGitAsync(string username, string email, string workDir)
    {
        await ExecuteGitCommandAsync($"config user.name \"{username}\"", workDir);
        await ExecuteGitCommandAsync($"config user.email \"{email}\"", workDir);
    }

    public async Task CommitChangesAsync(string message, string workDir)
    {
        await ExecuteGitCommandAsync("add .", workDir);
        await ExecuteGitCommandAsync($"commit -m \"{message}\"", workDir);
    }

    public async Task PushChangesAsync(string workDir)
    {
        var branch = await ExecuteGitCommandAsync("rev-parse --abbrev-ref HEAD", workDir);
        await ExecuteGitCommandAsync($"push origin {branch.Trim()}", workDir);
    }

    public async Task RebaseToMasterAsync(string workDir)
    {
        // Get current branch name before switching
        var currentBranch = await ExecuteGitCommandAsync("rev-parse --abbrev-ref HEAD", workDir);
        currentBranch = currentBranch.Trim();
        
        // Switch to master and update it
        await ExecuteGitCommandAsync("checkout master", workDir);
        await ExecuteGitCommandAsync("pull", workDir);
        
        // Switch back to feature branch and rebase onto master
        await ExecuteGitCommandAsync($"checkout {currentBranch}", workDir);
        await ExecuteGitCommandAsync("rebase master", workDir);
        
        // Switch back to master and merge (fast-forward)
        await ExecuteGitCommandAsync("checkout master", workDir);
        await ExecuteGitCommandAsync($"merge --ff-only {currentBranch}", workDir);
        await ExecuteGitCommandAsync("push", workDir);
    }

    private async Task<string> ExecuteGitCommandAsync(string command, string workDir)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = command,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Git command failed: {error}");
        }

        return output;
    }
}
