using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anitools.App.Startup;

/// <summary>
/// Команда <c>ani</c> без батника (docs/PLAN.md §4.12, этап 9). Установщик кладёт рядом с Anitools.exe жёсткую ссылку
/// ani.exe (та же программа, места не занимает) и добавляет папку программы в PATH пользователя. Оконная программа:
/// cmd и PowerShell её не ждут, а текущая папка консоли (или проводника — если набрать ani в адресной строке)
/// достаётся ей. Запущена под именем ani без аргументов — открывается текущая папка; консоль, из которой её
/// набрали, закрывается сама (настройка «Закрывать консоль после ani»).
/// </summary>
public static partial class AniCommand
{
    public const string Name = "ani";

    private const int AttachParentProcess = -1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const ushort KeyEvent = 1;
    private const ushort VkReturn = 0x0D;

    /// <summary>Запущены под именем ani (ani.exe)?</summary>
    public static bool IsAni(string? processPath) =>
        processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals(Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Аргументы запуска: ani без аргументов — текущая папка, остальное — как есть.</summary>
    public static string[] Arguments(string[] args, string? processPath, string currentDirectory) =>
        IsAni(processPath) && args.Length == 0 ? [currentDirectory] : args;

    /// <summary>
    /// Закрыть консоль, из которой набрали ani: подключиться к ней и «набрать» exit с Enter — оболочка (cmd, PowerShell)
    /// выйдет сама, окно или вкладка Windows Terminal закроется штатно. Не из консоли (ярлык, адресная строка
    /// проводника) — false, ничего не делается.
    /// </summary>
    public static bool CloseParentConsole()
    {
        if (!OperatingSystem.IsWindows() || !AttachConsole(AttachParentProcess))
        {
            return false;
        }

        try
        {
            using var input = CreateFile("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (input.IsInvalid)
            {
                return false;
            }

            var keys = KeyPresses("exit\r");
            return WriteConsoleInput(input, keys, (uint)keys.Length, out var written) && written == keys.Length;
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>Нажатия клавиш для консоли: на каждый символ — «нажата» и «отпущена»; «\r» — Enter.</summary>
    internal static InputRecord[] KeyPresses(string text) =>
        [.. text.SelectMany(c => new[] { Key(c, down: true), Key(c, down: false) })];

    private static InputRecord Key(char c, bool down) => new()
    {
        EventType = KeyEvent,
        KeyDown = down ? 1 : 0,
        RepeatCount = 1,
        VirtualKeyCode = c == '\r' ? VkReturn : char.IsAsciiLetter(c) ? (ushort)char.ToUpperInvariant(c) : (ushort)0,
        UnicodeChar = (ushort)c,
    };

    /// <summary>INPUT_RECORD с KEY_EVENT_RECORD внутри (20 байт).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public int KeyDown;

        [FieldOffset(8)]
        public ushort RepeatCount;

        [FieldOffset(10)]
        public ushort VirtualKeyCode;

        [FieldOffset(12)]
        public ushort VirtualScanCode;

        [FieldOffset(14)]
        public ushort UnicodeChar;

        [FieldOffset(16)]
        public uint ControlKeyState;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteConsoleInput(SafeFileHandle input, [In] InputRecord[] records, uint count, out uint written);
}
