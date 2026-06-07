using DKS.Migration.Agent.Models;

namespace DKS.Migration.Agent.Services;

/// <summary>
/// Imports PST files into an Outlook profile via COM automation.
/// Requires Outlook to be installed. Runs in the user session (not SYSTEM).
/// </summary>
public class PstImporter
{
    private readonly ILogger<PstImporter> _logger;

    public PstImporter(ILogger<PstImporter> logger) => _logger = logger;

    public async Task<bool> AttachPstAsync(string pstPath, string profileName, CancellationToken ct)
    {
        return await Task.Run(() => AttachPstViaCom(pstPath, profileName), ct);
    }

    public async Task<bool> ImportPstIntoFolderAsync(string pstPath, string profileName,
        string? targetFolderName, CancellationToken ct)
    {
        return await Task.Run(() => ImportViaCom(pstPath, profileName, targetFolderName), ct);
    }

    private bool AttachPstViaCom(string pstPath, string profileName)
    {
        try
        {
            // Use COM late-binding to avoid requiring Interop assembly at compile time
            var outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType == null)
            {
                _logger.LogError("Outlook not installed (COM ProgID not found)");
                return false;
            }

            dynamic outlook = Activator.CreateInstance(outlookType)!;
            dynamic ns = outlook.GetNamespace("MAPI");

            // AddStore attaches the PST
            ns.AddStore(pstPath);
            _logger.LogInformation("PST attached: {Path}", pstPath);

            // Cleanup
            System.Runtime.InteropServices.Marshal.ReleaseComObject(ns);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(outlook);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach PST via COM: {Path}", pstPath);
            return false;
        }
    }

    private bool ImportViaCom(string pstPath, string profileName, string? targetFolderName)
    {
        try
        {
            var outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType == null)
            {
                _logger.LogError("Outlook not installed");
                return false;
            }

            dynamic outlook = Activator.CreateInstance(outlookType)!;
            dynamic ns = outlook.GetNamespace("MAPI");

            // First attach the PST
            ns.AddStore(pstPath);

            // Find the store we just added
            dynamic stores = ns.Stores;
            dynamic? pstStore = null;
            for (int i = 1; i <= stores.Count; i++)
            {
                dynamic s = stores[i];
                if (s.FilePath?.ToString()?.Equals(pstPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    pstStore = s;
                    break;
                }
            }

            if (pstStore == null)
            {
                _logger.LogWarning("PST store not found after AddStore");
                return false;
            }

            // Find or create target folder in default store
            dynamic defaultStore = ns.DefaultStore;
            dynamic inbox = ns.GetDefaultFolder(6); // olFolderInbox = 6
            dynamic targetFolder;

            if (!string.IsNullOrEmpty(targetFolderName))
            {
                // Create the import folder if it doesn't exist
                dynamic rootFolder = defaultStore.GetRootFolder();
                targetFolder = GetOrCreateFolder(rootFolder, targetFolderName.TrimStart('/'));
            }
            else
            {
                targetFolder = inbox;
            }

            // Copy items from PST root
            dynamic pstRoot = pstStore.GetRootFolder();
            CopyFolderContents(pstRoot, targetFolder);

            // Remove PST after import
            ns.RemoveStore(pstRoot);

            System.Runtime.InteropServices.Marshal.ReleaseComObject(ns);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(outlook);

            _logger.LogInformation("PST import completed: {Path}", pstPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PST import failed: {Path}", pstPath);
            return false;
        }
    }

    private dynamic GetOrCreateFolder(dynamic parentFolder, string folderName)
    {
        try
        {
            return parentFolder.Folders[folderName];
        }
        catch
        {
            return parentFolder.Folders.Add(folderName);
        }
    }

    private void CopyFolderContents(dynamic sourceFolder, dynamic targetFolder)
    {
        try
        {
            // Copy all items in this folder
            dynamic items = sourceFolder.Items;
            for (int i = 1; i <= items.Count; i++)
            {
                try { items[i].Copy().Move(targetFolder); }
                catch { /* skip individual item failures */ }
            }

            // Recursively handle subfolders
            dynamic subFolders = sourceFolder.Folders;
            for (int i = 1; i <= subFolders.Count; i++)
            {
                dynamic sub = subFolders[i];
                dynamic subTarget = GetOrCreateFolder(targetFolder, sub.Name);
                CopyFolderContents(sub, subTarget);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error copying folder contents from {Folder}", (string)sourceFolder.Name);
        }
    }
}
