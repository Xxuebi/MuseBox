using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed partial class BoardRepository
{
    public async Task ApplyElementPositionsAsync(string drawerId,
        IReadOnlyList<BoardElementPosition> positions, CancellationToken cancellationToken = default)
    {
        if (positions.Count == 0) return;
        if (positions.Select(position => position.Id).Distinct(StringComparer.Ordinal).Count() != positions.Count ||
            positions.Any(position => !Enum.IsDefined(position.Kind) ||
                !double.IsFinite(position.X) || !double.IsFinite(position.Y)))
            throw new ArgumentException("无效或重复的元素位置。", nameof(positions));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            foreach (var position in positions)
            {
                var table = position.Kind switch
                {
                    BoardElementKind.Image => "items",
                    BoardElementKind.Text => "text_items",
                    BoardElementKind.Drawing => "drawing_items",
                    _ => throw new ArgumentOutOfRangeException(nameof(positions))
                };
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"UPDATE {table} SET x=$x,y=$y WHERE id=$id AND drawer_id=$drawer";
                command.Parameters.AddWithValue("$x", position.X);
                command.Parameters.AddWithValue("$y", position.Y);
                command.Parameters.AddWithValue("$id", position.Id);
                command.Parameters.AddWithValue("$drawer", drawerId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("排列元素已不存在或不属于当前画板。");
            }
            // Existing revision triggers participate in the same transaction.
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
