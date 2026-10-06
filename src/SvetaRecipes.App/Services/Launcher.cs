using System.Diagnostics;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.Services;

public static class Launcher
{
    /// <summary>Opens a file or folder with its default application (PDFs open in the viewer, ready to print).</summary>
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>A fresh path for a generated document, e.g. exports/Labels 2026-10-05 2130.pdf.</summary>
    public static string ExportPath(string name, string extension = ".pdf")
    {
        Directory.CreateDirectory(AppPaths.ExportsFolder);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return Path.Combine(AppPaths.ExportsFolder, $"{name} {DateTime.Now:yyyy-MM-dd HHmmss}{extension}");
    }
}
