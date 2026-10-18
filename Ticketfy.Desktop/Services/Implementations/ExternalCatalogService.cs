using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Polly;
using Polly.Timeout;

namespace Ticketfy.Services.Implementations;

// 1. Modelos de datos para la API
public class OffProductResponse
{
    [JsonPropertyName("product")]
    public OffProductDetails? Product { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }
}

public class OffProductDetails
{
    [JsonPropertyName("product_name")]
    public string? ProductName { get; set; }

    [JsonPropertyName("categories")]
    public string? Categories { get; set; } // Viene como string separado por comas

    [JsonPropertyName("image_front_url")]
    public string? ImageUrl { get; set; }
}

// 2. Source Generator para Native AOT (Obligatorio)
[JsonSerializable(typeof(OffProductResponse))]
public partial class ExternalCatalogJsonContext : JsonSerializerContext { }

// 3. El Cliente API
public class ExternalCatalogService : IExternalCatalogService
{
    private readonly HttpClient _httpClient;
    // Timeout estricto de 1.5 segundos para no frustrar al cajero
    private readonly AsyncTimeoutPolicy _timeoutPolicy = Policy.TimeoutAsync(TimeSpan.FromSeconds(1.5), TimeoutStrategy.Pessimistic);

    public ExternalCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://world.openfoodfacts.org/api/v2/");
    }

    public async Task<ProductDto?> FetchProductByBarcodeAsync(string barcode)
    {
        try
        {
            return await _timeoutPolicy.ExecuteAsync(async () =>
            {
                var response = await _httpClient.GetAsync($"product/{barcode}.json");
                if (!response.IsSuccessStatusCode) return null;

                var stream = await response.Content.ReadAsStreamAsync();
                var data = await JsonSerializer.DeserializeAsync(
                    stream,
                    ExternalCatalogJsonContext.Default.OffProductResponse);

                if (data?.Status != 1 || data.Product == null) return null;

                // Mapear al DTO interno de Ticketfy
                return new ProductDto(
                    Id: Guid.NewGuid().ToString(),
                    Barcode: barcode,
                    Name: data.Product.ProductName ?? "Producto Desconocido",
                    Cost: 0m,
                    Price: 0m,
                    Category: ParsePrimaryCategory(data.Product.Categories)
                );
            });
        }
        catch (Exception)
        {
            // Falla de red, timeout o no encontrado. Silenciar y retornar null.
            return null;
        }
    }

    private string ParsePrimaryCategory(string? rawCategories)
    {
        if (string.IsNullOrWhiteSpace(rawCategories)) return "General";
        var categories = rawCategories.Split(',');
        return categories[0].Trim();
    }
}
