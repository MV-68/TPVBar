using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TPVBar.PrintAgent.Printers;

/// <summary>Envío de datos RAW a la cola de impresión de Windows (winspool.drv).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinSpool
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DocInfo1
    {
        public nint DocName;
        public nint OutputFile;
        public nint DataType;
    }

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinter(string printerName, out nint handle, nint defaults);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(nint handle);

    [LibraryImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true)]
    private static partial int StartDocPrinter(nint handle, int level, DocInfo1* docInfo);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndDocPrinter(nint handle);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartPagePrinter(nint handle);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndPagePrinter(nint handle);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WritePrinter(nint handle, byte* buffer, int count, out int written);

    public static void WriteRaw(string printerName, string documentName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, 0))
        {
            throw new InvalidOperationException(
                $"No se puede abrir la impresora de Windows «{printerName}» (error {Marshal.GetLastPInvokeError()}). Compruebe el nombre exacto.");
        }

        var name = Marshal.StringToHGlobalUni(documentName);
        var dataType = Marshal.StringToHGlobalUni("RAW");
        try
        {
            var info = new DocInfo1 { DocName = name, OutputFile = 0, DataType = dataType };
            if (StartDocPrinter(handle, 1, &info) == 0)
            {
                throw new InvalidOperationException($"Windows rechazó el documento (error {Marshal.GetLastPInvokeError()}).");
            }

            try
            {
                if (!StartPagePrinter(handle))
                {
                    throw new InvalidOperationException($"No se pudo iniciar la página (error {Marshal.GetLastPInvokeError()}).");
                }

                int written;
                bool ok;
                fixed (byte* pointer = data)
                {
                    ok = WritePrinter(handle, pointer, data.Length, out written);
                }

                if (!ok || written != data.Length)
                {
                    throw new InvalidOperationException($"No se pudieron enviar todos los datos a la cola (error {Marshal.GetLastPInvokeError()}).");
                }

                _ = EndPagePrinter(handle);
            }
            finally
            {
                _ = EndDocPrinter(handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(dataType);
            _ = ClosePrinter(handle);
        }
    }
}
