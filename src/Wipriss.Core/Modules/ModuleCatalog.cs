namespace Wipriss.Core.Modules;

/// <summary>All modules Wipriss knows about, in the order the island shows them.</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleInfo> All { get; } =
    [
        new("notes",      "Заметки",     "", "Быстрые заметки, которые не пропадут", 1),
        new("clipboard",  "Буфер",       "", "Последние 20 копирований: текст и картинки", 1),
        new("reminders",  "Напоминания", "", "Плашка точно в назначенное время", 1),
        new("processes",  "Процессы",    "", "Запущенные программы", 2),
        new("ports",      "Порты",       "", "Кто занял порт, и кнопка «остановить»", 2),
        new("capture",    "Скриншоты",   "", "Снимок и мини-запись экрана по PrtScreen", 3),
        new("converter",  "Конвертер",   "", "Формат, размер и вес картинки, удаление фона", 4),
        new("reader",     "Документы",   "", "PDF, XLSX и CSV в лёгком окне", 5),
        new("dictation",  "Голос",       "", "Говоришь, и текст печатается", 6),
        new("cleaner",    "Очистка",     "", "Находит остатки удалённых программ и мусор", 7),
    ];

    public static ModuleInfo Get(string id) =>
        All.FirstOrDefault(m => m.Id == id) ?? throw new ArgumentException($"Unknown module '{id}'.", nameof(id));
}
