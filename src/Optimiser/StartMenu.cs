namespace Optimiser;

public static class StartMenu
{
    // Every app in the Start menu, desktop and Store alike, with the ID "shell:AppsFolder\<ID>" opens it by. Read
    // through the shell's Apps folder in this process: an elevated PowerShell would load modules from the user's own
    // Documents folder, where any program can put one.
    public static List<(string Name, string AppId)> Apps()
    {
        var apps = new List<(string, string)>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                var items = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}").Items(); // the Apps folder
                for (var i = 0; i < (int)items.Count; i++)
                {
                    var item = items.Item(i);
                    apps.Add(((string)item.Name, (string)item.Path));
                }
            }
            catch (Exception e) { error = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); // the shell's objects want a single-threaded apartment
        thread.Start();
        if (!thread.Join(15000)) throw new TimeoutException("Windows took too long to list the Start menu.");
        return error == null ? apps : throw error;
    }
}
