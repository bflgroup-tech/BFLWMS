using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wms.Data.Api;

namespace Wms.Web.Controllers.Api.V1;

[ApiController]
[Route("api/v1/store")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class StoreController(StoreGrnService grnService, StoreStocktakeService stocktakeService) : ControllerBase
{
    private static object Success(string message) => new { status = "success", message };
    private static object Failed(string message) => new { status = "failed", message };

    // EF Core's DbUpdateException.Message is a generic wrapper ("An error occurred
    // while saving...") — the actual SQL error is on the innermost exception.
    private static string ErrorMessage(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return inner.Message;
    }

    /// <summary>Store submits a GRN (goods receipt note): a header (store, datetime,
    /// GIN number, user) plus one or more transfers, each with its own received item lines.</summary>
    [HttpPost("grn")]
    public async Task<IActionResult> PostGrn([FromBody] StoreGrnRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.StoreId))
            return BadRequest(Failed("storeId is required"));
        if (string.IsNullOrWhiteSpace(req.GinNo))
            return BadRequest(Failed("ginNo is required"));
        if (string.IsNullOrWhiteSpace(req.User))
            return BadRequest(Failed("user is required"));
        if (req.Transfers is null || req.Transfers.Count == 0)
            return BadRequest(Failed("transfers must contain at least one entry"));
        if (req.Transfers.Any(t => string.IsNullOrWhiteSpace(t.TrfNo)))
            return BadRequest(Failed("every transfer requires a trfNo"));
        if (req.Transfers.Any(t => t.Items is null || t.Items.Count == 0))
            return BadRequest(Failed("every transfer must contain at least one item"));

        try
        {
            var result = await grnService.SubmitAsync(req, ct);

            if (result.Error is not null)
                return BadRequest(Failed(result.Error));

            return Ok(Success("GRN created successfully"));
        }
        catch (Exception ex)
        {
            return StatusCode(500, Failed(ErrorMessage(ex)));
        }
    }

    /// <summary>Store submits stocktake scan results: a header (stock count, store,
    /// date/time, username) plus scanned item lines (EAN, QR code, RFID, quantity).</summary>
    [HttpPost("stocktake")]
    public async Task<IActionResult> PostStocktake([FromBody] StoreStocktakeResultRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.StockCountId))
            return BadRequest(Failed("stockCountId is required"));
        if (string.IsNullOrWhiteSpace(req.StoreId))
            return BadRequest(Failed("storeId is required"));
        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(Failed("username is required"));
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(Failed("items must contain at least one entry"));
        if (req.Items.Any(i => string.IsNullOrWhiteSpace(i.ItemCode)))
            return BadRequest(Failed("every item requires an itemCode"));

        try
        {
            var result = await stocktakeService.SubmitResultAsync(req, ct);

            if (result.Error is not null)
                return BadRequest(Failed(result.Error));

            return Ok(Success("Stocktake created successfully"));
        }
        catch (Exception ex)
        {
            return StatusCode(500, Failed(ErrorMessage(ex)));
        }
    }

    /// <summary>Store submits stocktake data: a header (stock count, store, transaction
    /// date) plus counted item lines (quantity, SOH, variance).</summary>
    [HttpPost("stocktake/report")]
    public async Task<IActionResult> PostStocktakeReport([FromBody] StoreStocktakeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.StockCountId))
            return BadRequest(Failed("stockCountId is required"));
        if (string.IsNullOrWhiteSpace(req.StoreId))
            return BadRequest(Failed("storeId is required"));
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(Failed("items must contain at least one entry"));
        if (req.Items.Any(i => string.IsNullOrWhiteSpace(i.ItemCode)))
            return BadRequest(Failed("every item requires an itemCode"));

        try
        {
            var result = await stocktakeService.SubmitAsync(req, ct);

            if (result.Error is not null)
                return BadRequest(Failed(result.Error));

            return Ok(Success("Stocktake created successfully"));
        }
        catch (Exception ex)
        {
            return StatusCode(500, Failed(ErrorMessage(ex)));
        }
    }
}
