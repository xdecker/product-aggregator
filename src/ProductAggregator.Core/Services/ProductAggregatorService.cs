using System.Diagnostics;
using ProductAggregator.Core.Interfaces;
using ProductAggregator.Core.Models;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace ProductAggregator.Core.Services;

public class ProductAggregatorService : IProductAggregatorService
{
    private readonly IEnumerable<IPriceProvider> _priceProviders;
    private readonly IEnumerable<IStockProvider> _stockProviders;

    private readonly IDistributedCache _cache;
    private static readonly SemaphoreSlim _semaphore = new(10);

    public ProductAggregatorService(
        IEnumerable<IPriceProvider> priceProviders,
        IEnumerable<IStockProvider> stockProviders,
        IDistributedCache cache)
    {
        _priceProviders = priceProviders;
        _stockProviders = stockProviders;
        _cache = cache;
    }

    public async Task<AggregatedProductResponse> AggregateProductsAsync(
        AggregatedProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = new AggregatedProductResponse
        {
            TotalRequested = request.ProductIds.Count
        };



        var tasks = request.ProductIds.Select(async productId =>
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var product = await GetProductInternalAsync(productId, request, cancellationToken);
                return (product, error: (string?)null);
            }
            catch (Exception ex)
            {
                return (product: (Product?)null, error: $"Failed to process product {productId}, error: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            if (result.product != null)
            {
                response.Products.Add(result.product);
                response.TotalSuccessful++;
            }

            if (result.error != null)
            {
                response.Errors.Add(result.error);
            }
        }

        stopwatch.Stop();
        response.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        return response;
    }



    public async Task<Product?> GetProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        var request = new AggregatedProductRequest
        {
            ProductIds = new List<string> { productId }
        };

        return await GetProductInternalAsync(productId, request, cancellationToken);
    }

    private async Task<Product?> GetProductInternalAsync(string productId, AggregatedProductRequest request, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"product:{productId}:prices:{request.IncludePrices}:stock:{request.IncludeStock}";

        try
        {
            var cachedProduct = await _cache.GetStringAsync(cacheKey, cancellationToken);
            if (cachedProduct != null)
            {
                return JsonSerializer.Deserialize<Product>(cachedProduct);
            }

        }
        catch { }

        var product = new Product
        {
            Id = productId,
            Name = $"Product {productId}",
            Description = $"Description for product {productId}",
            Category = GetCategoryFromId(productId)
        };


        var priceTask = request.IncludePrices
        ? GetPricesAsync(productId, cancellationToken)
        : Task.FromResult(new List<PriceInfo>());

        var stockTask = request.IncludeStock
            ? GetStockAsync(productId, cancellationToken)
            : Task.FromResult(new List<StockInfo>());

        var prices = await priceTask;
        var stocks = await stockTask;

        product.Prices.AddRange(prices);
        product.StockLevels.AddRange(stocks);

        try
        {
            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            };

            var serializedProduct = JsonSerializer.Serialize(product);
            await _cache.SetStringAsync(cacheKey, serializedProduct, cacheOptions, cancellationToken);

        }
        catch { }

        return product;
    }

    private static string GetCategoryFromId(string productId)
    {
        var hash = Math.Abs(productId.GetHashCode());
        var categories = new[] { "Electronics", "Clothing", "Home", "Sports", "Books", "Toys" };
        return categories[hash % categories.Length];
    }

    private async Task<List<PriceInfo>> GetPricesAsync(
    string productId,
    CancellationToken cancellationToken)
    {
        var priceTasks = _priceProviders.Select(async provider =>
        {
            try
            {
                var response = await provider.GetPriceAsync(productId, cancellationToken);
                return response.Success ? response.PriceInfo : null;
            }
            catch
            {
                return null;
            }
        });

        var prices = await Task.WhenAll(priceTasks);

        return prices
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();
    }


    private async Task<List<StockInfo>> GetStockAsync(
    string productId,
    CancellationToken cancellationToken)
    {
        var stockTasks = _stockProviders.Select(async provider =>
        {
            try
            {
                var response = await provider.GetStockAsync(productId, cancellationToken);
                return response.Success ? response.StockInfos : null;
            }
            catch
            {
                return null;
            }
        });

        var stocks = await Task.WhenAll(stockTasks);

        return stocks
            .Where(s => s != null)
            .SelectMany(s => s!)
            .ToList();
    }
}
