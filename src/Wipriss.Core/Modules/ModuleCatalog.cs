namespace Wipriss.Core.Modules;

/// <summary>All modules Wipriss knows about, in the order the island shows them.</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleInfo> All { get; } =
    [
        new("notes",      "Заметки",     "", "Быстрые заметки по горячей клавише", 1, ModulePlace.Background),
        new("clipboard",  "Буфер",       "", "Последние 20 копирований", 1),
        new("reminders",  "Напоминания", "", "Плашка точно в срок", 1),
        new("network",    "Сеть",        "", "Кто тратит трафик", 2),
        new("ports",      "Порты",       "", "Кто занял порт", 2),
        new("capture",    "Скриншоты",   "", "Снимок по клавише PrtSc", 3, ModulePlace.Background),
        new("converter",  "Конвертер",   "", "Формат, размер и фон картинок", 4),
        new("reader",     "Таблицы",   "", "XLSX, CSV и другие таблицы", 5),
        new("dictation",  "Голос",       "", "Говорите, и текст печатается сам", 6, ModulePlace.Background),
        new("cleaner",    "Очистка",     "", "Мусор и остатки программ", 7),
        new("agents",     "Claude",        "", "Чем сейчас занят Claude Code", 8, ModulePlace.Card),
        new("media",      "Сейчас играет", "", "Музыка и видео из любых программ", 8, ModulePlace.Card),
    ];

    public static ModuleInfo Get(string id) =>
        All.FirstOrDefault(m => m.Id == id) ?? throw new ArgumentException($"Unknown module '{id}'.", nameof(id));
}
