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
    private readonly string? _sshKeyPath;
    private readonly string? _gitSshCommand;
    private readonly GitConfig? _gitConfig;

    public GitService()
    {
        _gitConfig = null;
        _sshKeyPath = null;
        _gitSshCommand = null;
    }

    public GitService(GitConfig gitConfig)
    {
        _gitConfig = gitConfig;

        // If SSH key is provided in settings, write it to ~/.ssh and configure
        if (!string.IsNullOrWhiteSpace(gitConfig.SshKey))
        {
            _sshKeyPath = SetupSshKey(gitConfig.SshKey);
            if (_sshKeyPath != null)
            {
                _gitSshCommand = $"ssh -i \"{_sshKeyPath}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null";
                Console.WriteLine($"SSH key configured from settings");
            }
        }

        // For HTTPS with credentials, configure git credential helper
        if (!string.IsNullOrWhiteSpace(gitConfig.RepositoryUrl) &&
            gitConfig.RepositoryUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            SetupHttpsCredentials(gitConfig);
        }
    }

    private static void SetupHttpsCredentials(GitConfig gitConfig)
    {
        try
        {
            // Configure git to store credentials
            ExecuteCommand("git", "config --global credential.helper store");

            // Determine the credential to use
            string? credential = null;
            string? username = null;

            if (!string.IsNullOrWhiteSpace(gitConfig.ApiToken))
            {
                username = "oauth2";
                credential = gitConfig.ApiToken;
                Console.WriteLine("Configuring git credentials with API token");
            }
            else if (!string.IsNullOrWhiteSpace(gitConfig.Password))
            {
                username = gitConfig.Username;
                credential = gitConfig.Password;
                Console.WriteLine("Configuring git credentials with username/password");
            }

            if (credential != null && username != null)
            {
                // Extract host from repo URL
                Uri uri = new Uri(gitConfig.RepositoryUrl);
                string host = uri.Host;

                // Write credentials to ~/.git-credentials
                string credentialsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".git-credentials");

                string credentialLine = $"https://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(credential)}@{host}\n";

                // Append if file exists, otherwise create
                File.AppendAllText(credentialsPath, credentialLine);

                if (!OperatingSystem.IsWindows())
                {
                    ExecuteCommand("chmod", $"600 {credentialsPath}");
                }

                Console.WriteLine($"Git credentials stored for {host}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to setup HTTPS credentials: {ex.Message}");
        }
    }

    private static string? SetupSshKey(string sshKeyContent)
    {
        try
        {
            Console.WriteLine($"SSH key from settings: {sshKeyContent.Length} chars");

            // Create .ssh directory if needed
            string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            if (!Directory.Exists(sshDir))
            {
                Directory.CreateDirectory(sshDir);

                // Set permissions on .ssh directory
                if (!OperatingSystem.IsWindows())
                {
                    ExecuteCommand("chmod", $"700 {sshDir}");
                }
            }

            // Normalize line endings to Unix (LF only) and ensure proper format
            // Also handle JSON unicode escapes that might not have been decoded
            string normalizedKey = sshKeyContent
                .Replace("\\u002B", "+")
                .Replace("\\u002b", "+")
                .Replace("\\u002F", "/")
                .Replace("\\u002f", "/")
                .Replace("\\n", "\n")
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();

            // Decode any remaining \uXXXX escapes
            normalizedKey = System.Text.RegularExpressions.Regex.Replace(
                normalizedKey,
                @"\\u([0-9A-Fa-f]{4})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

            if (!normalizedKey.EndsWith("\n"))
            {
                normalizedKey += "\n";
            }

            Console.WriteLine($"SSH key after normalization: {normalizedKey.Length} chars");
            Console.WriteLine($"SSH key starts with: {normalizedKey.Substring(0, Math.Min(50, normalizedKey.Length))}...");
            Console.WriteLine($"SSH key ends with: ...{normalizedKey.Substring(Math.Max(0, normalizedKey.Length - 50))}");

            // Detect key type from content
            string keyFileName = "id_rsa";
            if (normalizedKey.Contains("OPENSSH PRIVATE KEY"))
            {
                // New OpenSSH format - check if it's ed25519 by size (ed25519 keys are ~400-500 chars)
                // RSA keys in OpenSSH format are typically 2000+ chars
                if (normalizedKey.Length < 600)
                {
                    keyFileName = "id_ed25519";
                    Console.WriteLine("Detected ed25519 key format (based on size)");
                }
                else
                {
                    Console.WriteLine("Detected OpenSSH format RSA key");
                }
            }
            else if (normalizedKey.Contains("RSA PRIVATE KEY"))
            {
                Console.WriteLine("Detected PEM format RSA key");
            }

            // Write the key to a file
            string keyPath = Path.Combine(sshDir, keyFileName);

            // Write with explicit Unix line endings
            using (StreamWriter writer = new StreamWriter(keyPath, false, new System.Text.UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                writer.Write(normalizedKey);
            }

            // Verify what was written
            string written = File.ReadAllText(keyPath);
            Console.WriteLine($"Written to file: {written.Length} chars");

            // Set permissions to 600 on Linux
            if (!OperatingSystem.IsWindows())
            {
                ExecuteCommand("chmod", $"600 {keyPath}");
            }

            // Create SSH config to disable host key checking globally
            string configPath = Path.Combine(sshDir, "config");
            string sshConfig = $"Host *\n  StrictHostKeyChecking no\n  UserKnownHostsFile /dev/null\n  IdentityFile ~/.ssh/{keyFileName}\n";

            // Write config with explicit Unix line endings
            using (StreamWriter writer = new StreamWriter(configPath, false, new System.Text.UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                writer.Write(sshConfig);
            }

            if (!OperatingSystem.IsWindows())
            {
                ExecuteCommand("chmod", $"600 {configPath}");
            }

            Console.WriteLine($"SSH key written to {keyPath}");
            Console.WriteLine($"SSH config written to {configPath}");

            return keyPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to setup SSH key: {ex.Message}");
            return null;
        }
    }

    private static void ExecuteCommand(string command, string arguments)
    {
        Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        process.WaitForExit();
    }

    public async Task<string> CloneRepositoryAsync(string repoUrl, string workDir)
    {
        // For HTTPS URLs, inject credentials if available
        string effectiveUrl = GetEffectiveUrl(repoUrl);
        string result = await ExecuteGitCommandAsync($"clone {effectiveUrl} {workDir}", Directory.GetCurrentDirectory());
        return result;
    }

    private string GetEffectiveUrl(string repoUrl)
    {
        if (_gitConfig == null)
        {
            return repoUrl;
        }

        // Only modify HTTPS URLs
        if (!repoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return repoUrl;
        }

        // Try API token first (works with GitHub, GitLab, etc.)
        if (!string.IsNullOrWhiteSpace(_gitConfig.ApiToken))
        {
            // Format: https://oauth2:TOKEN@github.com/user/repo.git
            Uri uri = new Uri(repoUrl);
            string authUrl = $"https://oauth2:{_gitConfig.ApiToken}@{uri.Host}{uri.PathAndQuery}";
            Console.WriteLine($"Using API token for authentication");
            return authUrl;
        }

        // Try username/password
        if (!string.IsNullOrWhiteSpace(_gitConfig.Username) && !string.IsNullOrWhiteSpace(_gitConfig.Password))
        {
            Uri uri = new Uri(repoUrl);
            string authUrl = $"https://{Uri.EscapeDataString(_gitConfig.Username)}:{Uri.EscapeDataString(_gitConfig.Password)}@{uri.Host}{uri.PathAndQuery}";
            Console.WriteLine($"Using username/password for authentication");
            return authUrl;
        }

        return repoUrl;
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
        Process process = new Process
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

        // Set GIT_SSH_COMMAND if we have an SSH key
        if (_gitSshCommand != null)
        {
            process.StartInfo.EnvironmentVariables["GIT_SSH_COMMAND"] = _gitSshCommand;
        }

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Git command failed: {error}");
        }

        return output;
    }
}
