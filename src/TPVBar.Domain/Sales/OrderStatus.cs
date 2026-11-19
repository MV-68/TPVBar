namespace TPVBar.Domain.Sales;

public enum OrderStatus
{
    Open = 1,
    PendingPayment = 2,
    Paid = 3,
    Cancelled = 4,
}

public enum OrderLineStatus
{
    /// <summary>Añadida pero aún no enviada a cocina/barra: se puede modificar o quitar.</summary>
    Pending = 1,
    Sent = 2,
    Preparing = 3,
    Ready = 4,
    Delivered = 5,

    /// <summary>Anulada tras enviarse. Se conserva (con motivo) y no suma al total.</summary>
    Cancelled = 6,
}
