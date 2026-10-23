using QRCoder;

namespace KittyClaw.Web.Services;

internal static class RemoteAccessQrCode
{
    internal static string DataUrl(string url)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        return "data:image/png;base64," + Convert.ToBase64String(qr.GetGraphic(8));
    }
}
