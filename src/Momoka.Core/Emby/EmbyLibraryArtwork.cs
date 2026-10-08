namespace Momoka.Emby;

/// <summary>打开图片编辑器时固定媒体库和登录身份；离页后的委托不能继续写入。</summary>
public sealed class EmbyLibraryArtwork(EmbySessionScope scope, EmbyItem item, CancellationToken lifetime)
{
    public EmbyItem Item { get; } = item;
    public Task<RemoteImageResult> FindAsync(string type) => ReadAsync((client, token) => client.GetRemoteImagesAsync(Item.Id, type, token));
    public Task<byte[]> FetchAsync(string type, string tag, int width, int? index) => ReadAsync((client, token) => client.GetImageBytesAsync(Item.Id, type, tag, width, token, index));
    public Task<byte[]> FetchRemoteAsync(string url) => ReadAsync((client, token) => client.GetRemoteImageBytesAsync(url, token));
    public Task<EmbyItem> ReloadAsync() => ReadAsync((client, token) => client.GetItemAsync(Item.Id, token));
    public Task ApplyAsync(string type, int index, RemoteImageInfo image) => WriteAsync((client, token) => client.ApplyRemoteImageAsync(Item.Id, type, image.Url, image.ProviderName, token));
    public Task DeleteAsync(string type, int? index) => WriteAsync((client, token) => client.DeleteImageAsync(Item.Id, type, index, token));
    public Task UploadAsync(string type, byte[] bytes, string contentType) => WriteAsync((client, token) => client.UploadImageAsync(Item.Id, type, bytes, contentType, token));

    private async Task<T> ReadAsync<T>(Func<EmbyClient, CancellationToken, Task<T>> read)
    {
        lifetime.ThrowIfCancellationRequested(); scope.ThrowIfNotCurrent();
        var value = await scope.ExecuteAsync(read, lifetime).ConfigureAwait(false);
        lifetime.ThrowIfCancellationRequested(); scope.ThrowIfNotCurrent();
        return value;
    }
    private async Task WriteAsync(Func<EmbyClient, CancellationToken, Task> write)
    {
        lifetime.ThrowIfCancellationRequested(); scope.ThrowIfNotCurrent();
        await scope.ExecuteAsync(write, lifetime).ConfigureAwait(false);
        lifetime.ThrowIfCancellationRequested(); scope.ThrowIfNotCurrent();
    }
}
