using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed partial class BoardRepository
{
    public async Task<string> ImportSceneAsync(string? drawerId, PreparedScene scene, string filePath, CancellationToken cancellationToken = default)
    {
        SceneMigration.UpgradeToCurrent(scene.Document);
        SceneValidation.Validate(scene.Document);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            if (drawerId is null)
            {
                query.CommandText = "SELECT COALESCE(MAX(sort_order),-1)+1 FROM drawers";
                var order = Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken));
                var existing = await SceneRowsAsync<Drawer>(connection, transaction,
                    "SELECT id Id,sort_order SortOrder,created_utc CreatedUtc,display_name DisplayName FROM drawers", "", cancellationToken);
                drawerId = DrawerIdFromIndex(order);
                while (existing.Any(d => d.Id == drawerId)) drawerId = DrawerIdFromIndex(++order);
                await ExecuteSceneAsync(connection, transaction,
                    "INSERT INTO drawers(id,sort_order,created_utc,display_name) VALUES($id,$order,$created,$name)", cancellationToken,
                    ("$id", drawerId), ("$order", order), ("$created", DateTime.UtcNow.ToString("O")), ("$name", scene.Document.Name));
            }
            else
            {
                query.CommandText = "SELECT COUNT(*) FROM drawers WHERE id=$id";
                query.Parameters.AddWithValue("$id", drawerId);
                if (Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken)) != 1) throw new InvalidOperationException("目标抽屉已不存在。");
            }
            var assets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in scene.Document.Assets)
            {
                var found = (await SceneRowsAsync<AssetRecord>(connection, transaction, """
                    SELECT id Id,hash Hash,extension Extension,file_name FileName,pixel_width PixelWidth,pixel_height PixelHeight,created_utc CreatedUtc
                    FROM assets WHERE hash=$id
                    """, asset.Hash, cancellationToken)).SingleOrDefault();
                var id = found?.Id ?? Guid.NewGuid().ToString("N");
                var filename = found?.FileName ?? asset.Hash + asset.Extension;
                var destination = Path.Combine(_assetDirectory, filename);
                if (File.Exists(destination))
                {
                    if (await SceneFileService.HashFileAsync(destination, cancellationToken) != asset.Hash)
                        throw new InvalidDataException("本机图片资源校验失败；未替换画板。");
                }
                else
                {
                    var temporary = Path.Combine(_assetDirectory, $".scene-{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.Copy(scene.AssetPaths[asset.Id], temporary);
                        File.Move(temporary, destination);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                if (found is null)
                    await ExecuteSceneAsync(connection, transaction, """
                        INSERT INTO assets(id,hash,extension,file_name,pixel_width,pixel_height,created_utc)
                        VALUES($id,$hash,$ext,$file,$w,$h,$created)
                        """, cancellationToken, ("$id", id), ("$hash", asset.Hash), ("$ext", asset.Extension), ("$file", filename),
                        ("$w", asset.Width), ("$h", asset.Height), ("$created", DateTime.UtcNow.ToString("O")));
                assets.Add(asset.Id, id);
            }
            // Do not delete the drawer row: its identity and sidebar order survive.
            // Old immutable assets remain available to other drawers.
            foreach (var table in new[] { "items", "text_items", "drawing_items", "board_groups", "drawer_covers", "scene_bindings" })
                await ExecuteSceneAsync(connection, transaction, $"DELETE FROM {table} WHERE drawer_id=$id", cancellationToken, ("$id", drawerId));
            await ExecuteSceneAsync(connection, transaction, "UPDATE drawers SET display_name=$name WHERE id=$id", cancellationToken,
                ("$id", drawerId), ("$name", scene.Document.Name));
            var itemIds = new Dictionary<string, string>();
            var groupIds = scene.Document.Groups.ToDictionary(group => group.Id,
                _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
            foreach (var sourceGroup in scene.Document.Groups)
                await ExecuteSceneAsync(connection, transaction, """
                    INSERT INTO board_groups(id,drawer_id,parent_group_id,layer_name,background_color,border_color,
                        border_thickness,frame_padding,background_visible,locked,auto_membership)
                    VALUES($id,$drawer,$parent,$name,$background,$border,$thickness,$padding,$visible,$locked,$auto)
                    """, cancellationToken, ("$id", groupIds[sourceGroup.Id]), ("$drawer", drawerId),
                    ("$parent", sourceGroup.ParentGroupId.Length == 0 ? "" : groupIds[sourceGroup.ParentGroupId]),
                    ("$name", sourceGroup.LayerName), ("$background", sourceGroup.BackgroundColor),
                    ("$border", sourceGroup.BorderColor), ("$thickness", sourceGroup.BorderThickness),
                    ("$padding", sourceGroup.FramePadding), ("$visible", sourceGroup.BackgroundVisible),
                    ("$locked", sourceGroup.Locked), ("$auto", sourceGroup.AutoMembership));
            foreach (var source in scene.Document.Images.Cast<BoardElement>().Concat(scene.Document.Texts).Concat(scene.Document.Drawings))
            {
                var item = source.CloneElement();
                itemIds.Add(source.Id, item.Id = Guid.NewGuid().ToString("N"));
                item.DrawerId = drawerId;
                var group = item.GroupId.Length == 0 ? "" : groupIds[item.GroupId];
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                AddElementParameters(command, item);
                AddGroupParameters(command, item, group);
                if (item is BoardItem image)
                {
                    command.CommandText = """
                        INSERT INTO items(id,drawer_id,asset_id,x,y,width,height,rotation,z_index,created_utc,group_id,web_link,file_link,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name)
                        VALUES($id,$drawer,$asset,$x,$y,$w,$h,$rotation,$z,$created,$group,$web,$file,$groupBackground,$groupBorder,$groupThickness,$groupPadding,$groupVisible,$groupLocked,$groupAuto,$layerName)
                        """;
                    command.Parameters.AddWithValue("$asset", assets[image.AssetId]);
                    command.Parameters.AddWithValue("$web", image.WebLink);
                    command.Parameters.AddWithValue("$file", image.FileLink);
                }
                else if (item is BoardTextItem text)
                {
                    command.CommandText = """
                        INSERT INTO text_items(id,drawer_id,x,y,width,height,rotation,z_index,document_data,background_color,created_utc,web_link,file_link,group_id,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name)
                        VALUES($id,$drawer,$x,$y,$w,$h,$rotation,$z,$document,$background,$created,$web,$file,$group,$groupBackground,$groupBorder,$groupThickness,$groupPadding,$groupVisible,$groupLocked,$groupAuto,$layerName)
                        """;
                    command.Parameters.AddWithValue("$document", text.DocumentData);
                    command.Parameters.AddWithValue("$background", text.BackgroundColor);
                    command.Parameters.AddWithValue("$web", text.WebLink);
                    command.Parameters.AddWithValue("$file", text.FileLink);
                }
                else if (item is BoardDrawingItem drawing)
                {
                    command.CommandText = """
                        INSERT INTO drawing_items(id,drawer_id,x,y,width,height,rotation,z_index,kind,points_json,stroke_color,fill_color,stroke_thickness,stroke_opacity,dashed,created_utc,group_id,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name)
                        VALUES($id,$drawer,$x,$y,$w,$h,$rotation,$z,$kind,$points,$stroke,$fill,$thickness,$opacity,$dashed,$created,$group,$groupBackground,$groupBorder,$groupThickness,$groupPadding,$groupVisible,$groupLocked,$groupAuto,$layerName)
                        """;
                    command.Parameters.AddWithValue("$kind", (int)drawing.Kind);
                    command.Parameters.AddWithValue("$points", drawing.PointsJson);
                    command.Parameters.AddWithValue("$stroke", drawing.StrokeColor);
                    command.Parameters.AddWithValue("$fill", drawing.FillColor);
                    command.Parameters.AddWithValue("$thickness", drawing.StrokeThickness);
                    command.Parameters.AddWithValue("$opacity", drawing.StrokeOpacity);
                    command.Parameters.AddWithValue("$dashed", drawing.Dashed);
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            if (scene.Document.Cover is { } cover)
                await ExecuteSceneAsync(connection, transaction, """
                    INSERT INTO drawer_covers(drawer_id,source_asset_id,preview_asset_id,crop_json) VALUES($id,$source,$preview,$crop)
                    """, cancellationToken, ("$id", drawerId), ("$source", assets[cover.SourceAssetId]),
                    ("$preview", assets[cover.PreviewAssetId]), ("$crop", JsonSerializer.Serialize(cover.Crop)));
            foreach (var gif in scene.Document.Gifs)
                await ExecuteSceneAsync(connection, transaction,
                    "INSERT INTO gif_states(item_id,speed,is_playing,frame_index) VALUES($id,$speed,$playing,$frame)", cancellationToken,
                    ("$id", itemIds[gif.ItemId]), ("$speed", gif.Speed), ("$playing", gif.IsPlaying), ("$frame", gif.FrameIndex));
            var view = scene.Document.Viewport;
            await ExecuteSceneAsync(connection, transaction, """
                INSERT INTO viewports(drawer_id,pan_x,pan_y,zoom,window_left,window_top,window_width,window_height,topmost,background_color,window_opacity,opacity_affects_images,show_window_frame)
                VALUES($id,$x,$y,$zoom,$left,$top,$width,$height,$pin,$background,$opacity,$affect,$showFrame)
                ON CONFLICT(drawer_id) DO UPDATE SET pan_x=$x,pan_y=$y,zoom=$zoom,window_left=$left,window_top=$top,
                    window_width=$width,window_height=$height,topmost=$pin,background_color=$background,window_opacity=$opacity,opacity_affects_images=$affect,show_window_frame=$showFrame
                """, cancellationToken, ("$id", drawerId), ("$x", view.PanX), ("$y", view.PanY), ("$zoom", view.Zoom),
                ("$left", view.WindowLeft), ("$top", view.WindowTop), ("$width", view.WindowWidth), ("$height", view.WindowHeight),
                ("$pin", view.Topmost), ("$background", view.BackgroundColor), ("$opacity", view.WindowOpacity), ("$affect", view.OpacityAffectsImages),
                ("$showFrame", view.ShowWindowFrame));
            query.Parameters.Clear();
            query.CommandText = "SELECT COALESCE((SELECT revision FROM scene_revisions WHERE drawer_id=$id),0)";
            query.Parameters.AddWithValue("$id", drawerId);
            var revision = Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken));
            await WriteSceneBindingAsync(connection, transaction, new SceneBinding(drawerId, Path.GetFullPath(filePath), revision, scene.FileHash), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return drawerId;
        }
        finally { _gate.Release(); }
    }
}
