using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public interface IBoardRepository
{
    Task ApplyElementPositionsAsync(string drawerId, IReadOnlyList<BoardElementPosition> positions,
        CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<SceneSnapshot> CaptureSceneAsync(string drawerId, CancellationToken cancellationToken = default);
    Task<SceneBinding?> GetSceneBindingAsync(string drawerId, CancellationToken cancellationToken = default);
    Task MarkSceneSavedAsync(SceneBinding binding, CancellationToken cancellationToken = default);
    Task<string> ImportSceneAsync(string? drawerId, PreparedScene scene, string filePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GifSceneState>> GetGifStatesAsync(string drawerId, CancellationToken cancellationToken = default);
    Task SaveGifStatesAsync(IReadOnlyList<GifSceneState> states, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Drawer>> GetDrawersAsync(CancellationToken cancellationToken = default);
    Task<Drawer> AddNextDrawerAsync(CancellationToken cancellationToken = default);
    Task UpdateDrawerOrderAsync(IReadOnlyList<string> drawerIds, CancellationToken cancellationToken = default);
    Task UpdateDrawerNameAsync(string drawerId, string displayName, CancellationToken cancellationToken = default);
    Task UpdateDrawerCoverAsync(string drawerId, DrawerCover? cover, CancellationToken cancellationToken = default);
    Task<int> GetItemCountAsync(string drawerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> DeleteDrawerAsync(string drawerId, CancellationToken cancellationToken = default);
    Task<AssetRecord?> FindAssetByHashAsync(string hash, CancellationToken cancellationToken = default);
    Task UpsertAssetAsync(AssetRecord asset, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardItem>> GetItemsAsync(string drawerId, CancellationToken cancellationToken = default);
    Task AddItemsAsync(IReadOnlyList<BoardItem> items, CancellationToken cancellationToken = default);
    Task UpdateItemsAsync(IReadOnlyList<BoardItem> items, CancellationToken cancellationToken = default);
    Task DeleteItemsAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardTextItem>> GetTextItemsAsync(string drawerId, CancellationToken cancellationToken = default);
    Task AddTextItemsAsync(IReadOnlyList<BoardTextItem> items, CancellationToken cancellationToken = default);
    Task UpdateTextItemsAsync(IReadOnlyList<BoardTextItem> items, CancellationToken cancellationToken = default);
    Task DeleteTextItemsAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardDrawingItem>> GetDrawingItemsAsync(string drawerId, CancellationToken cancellationToken = default);
    Task AddDrawingItemsAsync(IReadOnlyList<BoardDrawingItem> items, CancellationToken cancellationToken = default);
    Task UpdateDrawingItemsAsync(IReadOnlyList<BoardDrawingItem> items, CancellationToken cancellationToken = default);
    Task DeleteDrawingItemsAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardGroup>> GetGroupsAsync(string drawerId, CancellationToken cancellationToken = default);
    Task ApplyLayerTreeAsync(string drawerId, IReadOnlyList<BoardGroup> groups,
        IReadOnlyList<BoardElement> elements, CancellationToken cancellationToken = default);
    Task<BoardViewport> GetViewportAsync(string drawerId, CancellationToken cancellationToken = default);
    Task SaveViewportAsync(BoardViewport viewport, CancellationToken cancellationToken = default);
    Task<string?> GetLatestAssetPathAsync(string drawerId, CancellationToken cancellationToken = default);
}
