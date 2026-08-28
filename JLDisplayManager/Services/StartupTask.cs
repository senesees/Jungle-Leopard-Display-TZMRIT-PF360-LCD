using System;
using System.Diagnostics;
using System.IO;
using System.Text;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services;

/// <summary>
/// Start-with-Windows, via a logon-triggered scheduled task.
///
/// A Run key would be one registry value, but it cannot restart the app if it
/// dies and cannot run elevated without prompting. A scheduled task does both,
/// and schtasks.exe is already on every Windows machine — no dependency needed
/// beyond generating the XML it wants.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "JungleLeopardDisplayManager";

    public static bool IsRegistered()
    {
        var (code, _) = Run($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static bool Register(out string error)
    {
        error = "";

        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            error = "could not determine this program's own path";
            return false;
        }

        string xmlPath = Path.Combine(Path.GetTempPath(), $"jl_startup_{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks reads the XML as UTF-16 with a BOM and nothing else.
            File.WriteAllText(xmlPath, BuildXml(exe), new UnicodeEncoding(false, true));

            var (code, output) = Run($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (code != 0)
            {
                error = string.IsNullOrWhiteSpace(output)
                    ? $"schtasks failed with code {code}"
                    : output.Trim();
                Storage.Log($"startup task registration failed: {error}");
                return false;
            }

            Storage.Log("registered the logon startup task");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp file; nothing to do */ }
        }
    }

    public static bool Unregister(out string error)
    {
        error = "";

        var (code, output) = Run($"/Delete /TN \"{TaskName}\" /F");

        // Deleting something that is not there is the desired end state, not a
        // failure worth showing the user.
        if (code != 0 && IsRegistered())
        {
            error = string.IsNullOrWhiteSpace(output) ? $"schtasks failed with code {code}" : output.Trim();
            return false;
        }

        Storage.Log("removed the logon startup task");
        return true;
    }

    /// <summary>Makes the registered state match the setting, creating or deleting as needed.</summary>
    public static bool Apply(bool wanted, out string error)
    {
        error = "";
        bool registered = IsRegistered();

        if (wanted == registered) return true;
        return wanted ? Register(out error) : Unregister(out error);
    }

    private static string BuildXml(string exePath)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string workingDir = Path.GetDirectoryName(exePath) ?? "";

        // StopIfGoingOnBatteries and friends default to true and would suspend a
        // display daemon on a laptop, which is exactly wrong for this. The
        // restart settings are the reason for choosing a task over a Run key.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Runs the Jungle Leopard Display manager at logon.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exePath)}</Command>
                  <Arguments>--startup</Arguments>
                  <WorkingDirectory>{Escape(workingDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static (int Code, string Output) Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return (-1, "could not start schtasks.exe");

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
