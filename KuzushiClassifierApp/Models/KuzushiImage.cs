namespace KuzushiClassifierApp.Models;

public sealed record KuzushiImage(
    string Id,
    string FileName,
    string MediaType,
    byte[] Bytes)
{
    public static KuzushiImage FromBytes(
        byte[] bytes,
        string? fileName = null,
        string mediaType = "application/octet-stream",
        string? id = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return new KuzushiImage(
            id ?? Guid.NewGuid().ToString("N"),
            fileName ?? "image",
            mediaType,
            bytes.ToArray());
    }

    public Stream OpenRead()
    {
        return new MemoryStream(Bytes, writable: false);
    }
}
