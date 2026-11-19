using Microsoft.Extensions.DependencyInjection;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Authentication;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Catalog;
using TPVBar.Application.Features.Inventory;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Application.Features.Kitchen;
using TPVBar.Application.Features.Orders;
using TPVBar.Application.Features.Printing;
using TPVBar.Application.Features.Reports;
using TPVBar.Domain.Security;

namespace TPVBar.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddTpvBarApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(LockoutPolicy.Default);
        services.AddScoped<LoginService>();
        services.AddScoped<UserAdministrationService>();

        services.AddScoped<CurrentUserAccessor>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserAccessor>());
        services.AddScoped<AuditWriter>();
        services.AddScoped<FloorService>();
        services.AddScoped<MenuService>();
        services.AddScoped<OrderService>();
        services.AddScoped<CatalogService>();

        services.AddScoped<PendingOperationEvents>();
        services.AddScoped<IOperationEvents>(sp => sp.GetRequiredService<PendingOperationEvents>());
        services.AddScoped<PrintEnqueuer>();
        services.AddScoped<KitchenService>();
        services.AddScoped<PrintAdminService>();
        services.AddScoped<PrintQueueService>();
        services.AddScoped<StockLedger>();
        services.AddScoped<StockService>();
        services.AddScoped<ReportService>();
        services.AddScoped<CashService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentMethodAdminService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<InvoiceReportService>();
        services.AddSingleton<BillingSubmissionLock>();
        services.AddScoped<BillingSubmissionService>();
        return services;
    }
}
