namespace TPVBar.Domain.Printing;

public enum PrinterType
{
    Receipt = 1,
    Kitchen = 2,
}

public enum PrinterConnection
{
    /// <summary>Cola de impresión de Windows (impresora instalada, compartida o USB con controlador).</summary>
    Windows = 1,

    /// <summary>Ethernet: socket TCP directo a la impresora (normalmente puerto 9100).</summary>
    Network = 2,

    /// <summary>USB directo sin controlador de Windows. Reservado: no implementado todavía.</summary>
    Usb = 3,

    /// <summary>Simulada: escribe un archivo de texto. NO es un dispositivo real y no saca papel.</summary>
    Simulated = 4,
}

public enum PrintJobType
{
    KitchenTicket = 1,
    KitchenVoid = 2,
    PreBill = 3,
    Test = 4,
    Receipt = 5,
    CashClose = 6,
}

public enum PrintJobStatus
{
    Pending = 1,
    Processing = 2,

    /// <summary>Entregado al dispositivo o a su cola. Ver <see cref="DeliveryLevel"/> para saber hasta dónde.</summary>
    Sent = 3,

    /// <summary>Agotados los reintentos. Requiere intervención (reintento manual).</summary>
    Failed = 4,

    Cancelled = 5,
}

/// <summary>Hasta dónde se sabe que ha llegado un trabajo. Los valores coinciden con los del contrato del agente.</summary>
public enum DeliveryLevel
{
    None = 0,
    Simulated = 1,
    SentToDevice = 2,
    AcceptedBySpooler = 3,
    Confirmed = 4,
}
