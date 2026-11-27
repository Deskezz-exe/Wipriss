using System.IO;

namespace Wipriss.Cleaner;

/// <summary>
/// Downloaded AI models, one item per model with its name («openai/whisper-large-v3»), so a needed model
/// is not deleted by mistake. Wipriss's own models live in the Wipriss folder and are never listed here.
/// </summary>
internal static class Models
{
    private const string OwnModels = "Модели самого Wipriss (голосовой ввод) лежат в папке Wipriss и в этот список не попадают.";

    public static IEnumerable<CleanGroup> Find(ScanContext context)
    {
        var cache = Path.Combine(Places.Profile, ".cache");

        // Hugging Face: hub\models--org--name, hub\datasets--org--name
        var hfHome = Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } home ? home : Path.Combine(cache, "huggingface");
        var hub = Environment.GetEnvironmentVariable("HF_HUB_CACHE") is { Length: > 0 } hubVar ? hubVar : Path.Combine(hfHome, "hub");
        context.Claim(hfHome);
        var hf = Finds.Children(hub)
            .Select(dir => (dir, name: Path.GetFileName(dir)))
            .Where(x => x.name.StartsWith("models--", StringComparison.OrdinalIgnoreCase) || x.name.StartsWith("datasets--", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                bool dataset = x.name.StartsWith("datasets--", StringComparison.OrdinalIgnoreCase);
                var repo = x.name[(x.name.IndexOf("--", StringComparison.Ordinal) + 2)..].Replace("--", "/");
                return Model(dataset ? repo + " (набор данных)" : repo, x.dir,
                    dataset ? $"Набор данных «{repo}» с Hugging Face." : $"Модель ИИ «{repo}» с Hugging Face.");
            });
        var hfGroup = Finds.Group("models:huggingface", "Hugging Face", CleanCategory.Models, "скачанные модели", "Hugging Face", hf);
        if (hfGroup is not null) yield return hfGroup;

        // PyTorch hub: checkpoints are single files, repositories are folders.
        var torchHub = Path.Combine(cache, @"torch\hub");
        context.Claim(Path.Combine(cache, "torch"));
        var torch = Finds.FilesIn(Path.Combine(torchHub, "checkpoints"), "*")
            .Select(f => Model(Path.GetFileName(f), f, $"Веса модели «{Path.GetFileName(f)}», скачанные PyTorch."))
            .Concat(Finds.Children(torchHub).Where(d => !Path.GetFileName(d).Equals("checkpoints", StringComparison.OrdinalIgnoreCase))
                .Select(d => Model(Path.GetFileName(d), d, $"Код модели «{Path.GetFileName(d)}» из PyTorch Hub.")));
        var torchGroup = Finds.Group("models:torch", "PyTorch", CleanCategory.Models, "скачанные модели", "PyTorch", torch);
        if (torchGroup is not null) yield return torchGroup;

        // Whisper (openai-whisper): one .pt per model size.
        var whisperDir = Path.Combine(cache, "whisper");
        context.Claim(whisperDir);
        var whisper = Finds.FilesIn(whisperDir, "*.pt")
            .Select(f => Model("Whisper " + Path.GetFileNameWithoutExtension(f), f, $"Модель распознавания речи Whisper «{Path.GetFileNameWithoutExtension(f)}»."));
        var whisperGroup = Finds.Group("models:whisper", "Whisper", CleanCategory.Models, "скачанные модели", "OpenAI", whisper);
        if (whisperGroup is not null) yield return whisperGroup;

        // LM Studio: models\<publisher>\<model>
        foreach (var root in new[] { Path.Combine(Places.Profile, @".lmstudio\models"), Path.Combine(cache, @"lm-studio\models") })
        {
            var models = Finds.Children(root).SelectMany(publisher => Finds.Children(publisher)
                .Select(m => Model($"{Path.GetFileName(publisher)}/{Path.GetFileName(m)}", m, $"Модель «{Path.GetFileName(m)}» для LM Studio.")));
            var group = Finds.Group("models:lmstudio:" + PathText.NameKey(root), "LM Studio", CleanCategory.Models,
                Finds.Installed(context.Inventory, "LM Studio"), "LM Studio", models);
            if (group is not null) yield return group;
        }

        // Ollama keeps models as shared pieces: only «ollama rm» may delete them.
        var ollama = Path.Combine(Places.Profile, @".ollama\models");
        var names = Finds.Children(Path.Combine(ollama, @"manifests\registry.ollama.ai\library"))
            .SelectMany(m => Finds.FilesIn(m, "*").Select(tag => $"{Path.GetFileName(m)}:{Path.GetFileName(tag)}"))
            .ToList();
        if (names.Count > 0)
        {
            var item = Finds.Item("Все модели Ollama", CleanColor.Yellow,
                "Модели Ollama хранятся общими кусками: одна папка на все модели.",
                "Удалённую модель придётся скачать заново.",
                [Path.Combine(ollama, "blobs")],
                "Удаляйте по одной командой «ollama rm <имя>» (Ollama сама уберёт ненужные куски).");
            if (item is not null)
            {
                item.Evidence.Add("Модели: " + string.Join(", ", names));
                item.Evidence.Add(OwnModels);
                yield return new CleanGroup { Id = "models:ollama", Title = "Ollama", Category = CleanCategory.Models,
                    Status = Finds.Installed(context.Inventory, "Ollama", female: true), Publisher = "Ollama", Items = [item] };
            }
        }
    }

    private static CleanItem? Model(string title, string path, string what)
    {
        var item = Finds.Item(title, CleanColor.Yellow, what,
            "Программа, которой нужна эта модель, скачает её заново при следующем запуске. Большие модели качаются долго.",
            [path], confidence: "средняя");
        if (item is null) return null;
        item.Action = CleanAction.Whole;
        if (item.LastChanged is { } changed)
            item.Evidence.Add($"Скачана или обновлена {changed:dd.MM.yyyy} ({(int)(DateTime.Now - changed).TotalDays} дн. назад)");
        item.Evidence.Add(OwnModels);
        return item;
    }
}
