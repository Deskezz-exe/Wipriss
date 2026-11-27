using System.Diagnostics;
using System.IO;

namespace Wipriss.Cleaner;

/// <summary>Setup files (.exe, .msi) in Downloads: green when that program is already installed.</summary>
internal static class Installers
{
    public static CleanGroup? Find(ScanContext context)
    {
        var downloads = Places.Downloads;
        if (!Directory.Exists(downloads)) return null;
        var items = new List<CleanItem>();
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(downloads).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        foreach (var file in files)
        {
            string? product = null, company = null;
            if (file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(file.FullName);
                    product = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
                    company = info.CompanyName;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            var match = Match(context.Inventory, product) ?? Match(context.Inventory, Path.GetFileNameWithoutExtension(file.Name));
            var days = (int)(DateTime.Now - file.LastWriteTime).TotalDays;
            var item = new CleanItem
            {
                Title = file.Name,
                Paths = [file.FullName],
                Size = file.Length,
                Files = 1,
                LastChanged = file.LastWriteTime,
                What = string.IsNullOrWhiteSpace(product) ? "Установщик программы." : $"Установщик «{product.Trim()}»" + (string.IsNullOrWhiteSpace(company) ? "." : $" от {company.Trim()}."),
            };
            item.Evidence.Add($"Скачан {file.LastWriteTime:dd.MM.yyyy} ({days} дн. назад)");
            if (match is not null)
            {
                item.Color = CleanColor.Green;
                item.Confidence = "высокая";
                item.IfDeleted = "Программа уже установлена. Если понадобится снова, установщик можно скачать с сайта (обычно там новее).";
                item.Evidence.Add($"Программа «{match.Name}» уже установлена");
            }
            else
            {
                item.Color = CleanColor.Yellow;
                item.Confidence = "средняя";
                item.IfDeleted = "Программа не найдена среди установленных: возможно, вы ещё собираетесь её поставить, или это не установщик.";
                item.Evidence.Add("Такой программы среди установленных нет");
            }
            items.Add(item);
        }
        return items.Count == 0 ? null : new CleanGroup
        {
            Id = "installers", Title = "Установщики в «Загрузках»", Category = CleanCategory.Installers,
            Status = downloads, Items = items.OrderByDescending(i => i.Color == CleanColor.Green).ThenByDescending(i => i.Size).ToList(),
        };
    }

    private static InstalledProgram? Match(Inventory inventory, string? name)
    {
        // «ChromeSetup», «VSCodeUserSetup-x64» → «chrome», «vscodeuser»
        var key = PathText.NameKey(name).Replace("installer", "").Replace("install", "").Replace("setup", "").Replace("x64", "");
        if (key.Length < 4) return null;
        return inventory.Programs.FirstOrDefault(p => p.NameKey.Length >= 4 && PathText.NamesMatch(key, p.NameKey));
    }
}
