using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Orders;

/// <summary>Carta vendible: categorías y productos activos con tipo impositivo vigente.</summary>
public sealed class MenuService(IAppData data, ICurrentUser user, TimeProvider time)
{
    public async Task<MenuView> GetMenuAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);

        var today = BusinessTime.Today(time);
        var categories = await data.ToListAsync(data.Categories.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name), cancellationToken);
        var products = await data.ToListAsync(data.Products.Where(p => p.IsActive).OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name), cancellationToken);
        var validRateIds = (await data.ToListAsync(data.TaxRates, cancellationToken))
            .Where(t => t.IsValidOn(today))
            .Select(t => t.Id)
            .ToHashSet();

        var menu = categories
            .Select(c => new CategoryMenu(
                c.Id,
                c.Name,
                products.Where(p => p.CategoryId == c.Id && validRateIds.Contains(p.TaxRateId))
                    .Select(p => new ProductTile(p.Id, p.Name, p.SalePrice, p.Station))
                    .ToList()))
            .Where(c => c.Products.Count > 0)
            .ToList();

        return new MenuView(menu);
    }
}
