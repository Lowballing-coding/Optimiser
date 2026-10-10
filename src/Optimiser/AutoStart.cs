using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace Optimiser;

// "Start with Windows" as a Task Scheduler task: it can start elevated without a UAC prompt, and unlike the
// schtasks defaults it also starts on battery and never gets stopped after 3 days.
public static class AutoStart
{
    public static string TaskName = "Optimiser"; // the self-checks use their own name and folder

    // The task starts Optimiser as admin, so it runs a copy in Program Files, which only admins can change,
    // rather than a file in Downloads that any program could swap.
    public static string InstallFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Optimiser");
    public static string InstalledExe => Path.Combine(InstallFolder, "Optimiser.exe");

    public static bool IsOn()
    {
        try
        {
            Folder().GetTask(TaskName);
            return true;
        }
        catch (COMException) { return false; }
    }

    // Also run on every start while it's on, so a newer download replaces the installed copy.
    public static void TurnOn(string exe)
    {
        if (!IsInstalled(exe))
        {
            Directory.CreateDirectory(InstallFolder);
            for (var i = 0; ; i++)
            {
                try { File.Copy(exe, InstalledExe, overwrite: true); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); } // the copy being replaced is still closing
            }
        }

        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Optimiser in the tray so it can switch to its gaming profile.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(InstalledExe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        const int CreateOrUpdate = 6, InteractiveToken = 3;
        Folder().RegisterTask(TaskName, xml, CreateOrUpdate, null, null, InteractiveToken, null);
    }

    public static void TurnOff()
    {
        try { Folder().DeleteTask(TaskName, 0); }
        catch (COMException) { } // already gone
        try
        {
            File.Delete(InstalledExe);
            Directory.Delete(InstallFolder); // only if empty
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // it's the copy that's running
    }

    static bool IsInstalled(string exe) => string.Equals(Path.GetFullPath(exe), InstalledExe, StringComparison.OrdinalIgnoreCase);

    static dynamic Folder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service.GetFolder(@"\");
    }
}
