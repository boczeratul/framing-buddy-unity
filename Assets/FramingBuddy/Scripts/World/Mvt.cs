using System;
using System.Collections.Generic;
using System.Text;

namespace FramingBuddy
{
    /// <summary>
    /// 精簡的 Mapbox Vector Tile（protobuf）解碼器：解出指定圖層的點或多邊形要素與屬性，
    /// 用於 OpenFreeMap 的 mountain_peak（山峰）與 building（遠景高樓）圖層。
    /// </summary>
    public static class Mvt
    {
        public class PointFeature
        {
            public Dictionary<string, object> props = new();
            /// <summary>圖磚內座標（0..extent）</summary>
            public int x, y;
        }

        public class PolygonFeature
        {
            public Dictionary<string, object> props = new();
            /// <summary>每個多邊形＝[外環, 內環…]，圖磚內座標，不含重複的收尾點</summary>
            public List<List<List<(int x, int y)>>> polygons = new();
        }

        ref struct Reader
        {
            readonly ReadOnlySpan<byte> buf;
            public int pos;
            public Reader(ReadOnlySpan<byte> b) { buf = b; pos = 0; }
            public bool Done => pos >= buf.Length;

            public ulong Varint()
            {
                ulong result = 0;
                int shift = 0;
                while (true)
                {
                    byte b = buf[pos++];
                    result |= (ulong)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0) return result;
                    shift += 7;
                }
            }

            public ReadOnlySpan<byte> Bytes()
            {
                int len = (int)Varint();
                var s = buf.Slice(pos, len);
                pos += len;
                return s;
            }

            public float Fixed32()
            {
                float v = BitConverter.ToSingle(buf.Slice(pos, 4).ToArray(), 0);
                pos += 4;
                return v;
            }

            public double Fixed64()
            {
                double v = BitConverter.ToDouble(buf.Slice(pos, 8).ToArray(), 0);
                pos += 8;
                return v;
            }

            public void Skip(int wire)
            {
                switch (wire)
                {
                    case 0: Varint(); break;
                    case 1: pos += 8; break;
                    case 2: pos += (int)Varint(); break;
                    case 5: pos += 4; break;
                    default: throw new Exception("bad wire type " + wire);
                }
            }
        }

        static long ZigZag(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

        class RawFeature
        {
            public Dictionary<string, object> props;
            public List<uint> geom;
            public int type;
        }

        static List<RawFeature> Layer(byte[] data, string layerName, out int extent)
        {
            extent = 4096;
            var result = new List<RawFeature>();
            var r = new Reader(data);
            while (!r.Done)
            {
                ulong tag = r.Varint();
                if ((tag >> 3) == 3 && (tag & 7) == 2)
                {
                    if (DecodeLayer(r.Bytes(), layerName, out int ext, result)) extent = ext;
                }
                else r.Skip((int)(tag & 7));
            }
            return result;
        }

        public static List<PointFeature> DecodePoints(byte[] data, string layerName, out int extent)
        {
            var result = new List<PointFeature>();
            foreach (var f in Layer(data, layerName, out extent))
            {
                if (f.type != 1 || f.geom.Count < 3) continue;
                // MoveTo(1) + dx, dy
                result.Add(new PointFeature { props = f.props, x = (int)ZigZag(f.geom[1]), y = (int)ZigZag(f.geom[2]) });
            }
            return result;
        }

        /// <summary>多邊形要素：依 MVT 規範以環的正負面積分出外環（圖磚座標下順時針）與內環</summary>
        public static List<PolygonFeature> DecodePolygons(byte[] data, string layerName, out int extent)
        {
            var result = new List<PolygonFeature>();
            foreach (var f in Layer(data, layerName, out extent))
            {
                if (f.type != 3) continue;
                var feat = new PolygonFeature { props = f.props };
                var rings = new List<List<(int, int)>>();
                int x = 0, y = 0, i = 0;
                List<(int, int)> cur = null;
                while (i < f.geom.Count)
                {
                    uint cmd = f.geom[i++];
                    int id = (int)(cmd & 7), count = (int)(cmd >> 3);
                    if (id == 1 || id == 2)
                    {
                        for (int k = 0; k < count && i + 1 < f.geom.Count; k++)
                        {
                            x += (int)ZigZag(f.geom[i++]);
                            y += (int)ZigZag(f.geom[i++]);
                            if (id == 1)
                            {
                                cur = new List<(int, int)>();
                                rings.Add(cur);
                            }
                            cur?.Add((x, y));
                        }
                    }
                    else if (id == 7) cur = null;
                }
                List<List<(int, int)>> poly = null;
                foreach (var ring in rings)
                {
                    if (ring.Count < 3) continue;
                    long area = 0;
                    for (int a = 0, b = ring.Count - 1; a < ring.Count; b = a++) area += (long)ring[b].Item1 * ring[a].Item2 - (long)ring[a].Item1 * ring[b].Item2;
                    if (area > 0 || poly == null)
                    {
                        poly = new List<List<(int, int)>> { ring };
                        feat.polygons.Add(poly);
                    }
                    else poly.Add(ring);
                }
                if (feat.polygons.Count > 0) result.Add(feat);
            }
            return result;
        }

        static bool DecodeLayer(ReadOnlySpan<byte> bytes, string wanted, out int extent, List<RawFeature> output)
        {
            extent = 4096;
            var r = new Reader(bytes);
            string name = null;
            var keys = new List<string>();
            var values = new List<object>();
            var features = new List<(List<uint> tags, List<uint> geom, int type)>();
            while (!r.Done)
            {
                ulong tag = r.Varint();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 1 && wire == 2) name = Encoding.UTF8.GetString(r.Bytes());
                else if (field == 2 && wire == 2) features.Add(DecodeFeature(r.Bytes()));
                else if (field == 3 && wire == 2) keys.Add(Encoding.UTF8.GetString(r.Bytes()));
                else if (field == 4 && wire == 2) values.Add(DecodeValue(r.Bytes()));
                else if (field == 5 && wire == 0) extent = (int)r.Varint();
                else r.Skip(wire);
            }
            if (name != wanted) return false;
            foreach (var (tags, geom, type) in features)
            {
                var f = new RawFeature { props = new Dictionary<string, object>(), geom = geom, type = type };
                for (int i = 0; i + 1 < tags.Count; i += 2)
                    if (tags[i] < keys.Count && tags[i + 1] < values.Count) f.props[keys[(int)tags[i]]] = values[(int)tags[i + 1]];
                output.Add(f);
            }
            return true;
        }

        static (List<uint>, List<uint>, int) DecodeFeature(ReadOnlySpan<byte> bytes)
        {
            var r = new Reader(bytes);
            var tags = new List<uint>();
            var geom = new List<uint>();
            int type = 0;
            while (!r.Done)
            {
                ulong tag = r.Varint();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 2 && wire == 2)
                {
                    var p = new Reader(r.Bytes());
                    while (!p.Done) tags.Add((uint)p.Varint());
                }
                else if (field == 3 && wire == 0) type = (int)r.Varint();
                else if (field == 4 && wire == 2)
                {
                    var p = new Reader(r.Bytes());
                    while (!p.Done) geom.Add((uint)p.Varint());
                }
                else r.Skip(wire);
            }
            return (tags, geom, type);
        }

        static object DecodeValue(ReadOnlySpan<byte> bytes)
        {
            var r = new Reader(bytes);
            object v = null;
            while (!r.Done)
            {
                ulong tag = r.Varint();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                switch (field)
                {
                    case 1 when wire == 2: v = Encoding.UTF8.GetString(r.Bytes()); break;
                    case 2 when wire == 5: v = (double)r.Fixed32(); break;
                    case 3 when wire == 1: v = r.Fixed64(); break;
                    case 4: v = (double)(long)r.Varint(); break;
                    case 5: v = (double)r.Varint(); break;
                    case 6: v = (double)ZigZag(r.Varint()); break;
                    case 7: v = r.Varint() != 0; break;
                    default: r.Skip(wire); break;
                }
            }
            return v;
        }
    }
}
