using System.IO;
using System.Security.Cryptography;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.Behavior;
using DesktopPet.Character;
using Image = System.Windows.Controls.Image;

namespace DesktopPet;

public sealed class SpritePlayer(Image image, long cacheBudgetBytes = 64L * 1024 * 1024)
{
    private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapImage> _contentCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hashByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly long _budget = cacheBudgetBytes > 0 ? cacheBudgetBytes : throw new ArgumentOutOfRangeException(nameof(cacheBudgetBytes));
    public long CachedPixelBytes { get; private set; }
    public int CachedImageCount => _contentCache.Count;
    private string? _shown;
    private int _facing;
    private FrameDefinition? _transformFrame;

    public void Show(ActionRunner runner, string? framePath = null)
    {
        var path = framePath ?? runner.CurrentFramePath;
        if (_shown != path)
        {
            if (!_cache.TryGetValue(path, out var bitmap))
            {
                // Animation packs can repeat identical PNGs under different frame names.
                // Share the decoded image without changing files, timing or image quality.
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                if (!_contentCache.TryGetValue(hash, out bitmap))
                {
                    stream.Position = 0;
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    var bytes = PixelBytes(bitmap);
                    if (bytes <= _budget)
                    {
                        while (CachedPixelBytes + bytes > _budget && _recent.Last is { } oldest)
                            Evict(oldest.Value);
                        _contentCache.Add(hash, bitmap);
                        CachedPixelBytes += bytes;
                        _nodes.Add(hash, _recent.AddFirst(hash));
                    }
                }
                if (_contentCache.ContainsKey(hash))
                {
                    _cache.Add(path, bitmap);
                    _hashByPath[path] = hash;
                }
            }
            if (_hashByPath.TryGetValue(path, out var shownHash) && _nodes.TryGetValue(shownHash, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
            }
            image.Source = bitmap;
            _shown = path;
        }
        var frame = runner.CurrentFrame;
        if (_facing != runner.Facing || !ReferenceEquals(_transformFrame, frame))
        {
            var transforms = new TransformGroup();
            transforms.Children.Add(new ScaleTransform(runner.Facing * frame.ScaleX, frame.ScaleY));
            transforms.Children.Add(new RotateTransform(frame.Rotation * runner.Facing));
            image.RenderTransform = transforms;
            _facing = runner.Facing;
            _transformFrame = frame;
        }
    }

    private static long PixelBytes(BitmapImage bitmap) =>
        (long)bitmap.PixelHeight * ((bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7L) / 8);

    private void Evict(string hash)
    {
        var bitmap = _contentCache[hash];
        foreach (var path in _cache.Where(pair => ReferenceEquals(pair.Value, bitmap)).Select(pair => pair.Key).ToArray())
        {
            _cache.Remove(path);
            _hashByPath.Remove(path);
        }
        _contentCache.Remove(hash);
        _recent.Remove(_nodes[hash]);
        _nodes.Remove(hash);
        CachedPixelBytes -= PixelBytes(bitmap);
    }
}
