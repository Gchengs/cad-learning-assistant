using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CadLearningAssistant.Services
{
    // 在一张图上推算“该往哪里点一下”的坐标。
    //
    // 用途：填充（-HATCH）这类命令要求必须在封闭区域内部给一个点，
    // 而初学者既看不出哪块区域是闭合的，也不知道点在哪才算“内部”。
    // 这里把模型空间里所有闭合曲线（圆、闭合多段线、闭合样条）采样成多边形，
    // 用奇偶规则求“真正在区域里、且离边界最远”的那个点，
    // 既可以直接当成坐标送进命令行，也可以画成箭头让人照着点。
    //
    // 边界条件全部写成明确的失败原因，绝不返回一个猜出来的点。
    internal static class ClickPointFinder
    {
        private const int GridCount = 48;
        private const int MinSamples = 160;
        private const int MaxSamples = 1200;
        private const int MaxCurves = 400;

        internal sealed class SearchResult
        {
            internal bool Found;
            internal Point3d Point;
            internal double Margin;
            internal double ArrowLength;
            internal int CurveCount;
            internal string Note = string.Empty;
        }

        internal static SearchResult Find(Database database)
        {
            SearchResult result = new SearchResult();
            if (database == null)
            {
                result.Note = "当前没有可用图纸。";
                return result;
            }

            List<Point2d[]> polygons = new List<Point2d[]>();
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    if (table == null)
                    {
                        result.Note = "取不到块表。";
                        return result;
                    }
                    BlockTableRecord space = transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                    if (space == null)
                    {
                        result.Note = "取不到模型空间。";
                        return result;
                    }

                    foreach (ObjectId id in space)
                    {
                        if (polygons.Count >= MaxCurves)
                        {
                            break;
                        }
                        Entity entity = null;
                        try
                        {
                            entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        Curve curve = entity as Curve;
                        if (curve == null)
                        {
                            continue;
                        }
                        bool closed;
                        try
                        {
                            closed = curve.Closed;
                        }
                        catch (Exception)
                        {
                            closed = false;
                        }
                        if (!closed)
                        {
                            continue;
                        }
                        Point2d[] polygon = Sample(curve);
                        if (polygon == null || polygon.Length < 4)
                        {
                            continue;
                        }
                        polygons.Add(polygon);
                        for (int index = 0; index < polygon.Length; index++)
                        {
                            if (polygon[index].X < minX) { minX = polygon[index].X; }
                            if (polygon[index].Y < minY) { minY = polygon[index].Y; }
                            if (polygon[index].X > maxX) { maxX = polygon[index].X; }
                            if (polygon[index].Y > maxY) { maxY = polygon[index].Y; }
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (Exception exception)
            {
                result.Note = "读取图形失败：" + exception.Message;
                return result;
            }

            if (polygons.Count == 0)
            {
                result.Note = "模型空间里没有闭合曲线（圆、闭合多段线或闭合样条），推不出内部点。";
                return result;
            }
            result.CurveCount = polygons.Count;

            double width = maxX - minX;
            double height = maxY - minY;
            if (!(width > 0) || !(height > 0))
            {
                result.Note = "闭合曲线的平面范围无效（宽或高为 0）。";
                return result;
            }

            double diagonal = Math.Sqrt(width * width + height * height);
            double bestMargin = -1;
            double bestX = 0;
            double bestY = 0;
            for (int row = 0; row < GridCount; row++)
            {
                double y = minY + height * (row + 0.5) / GridCount;
                for (int column = 0; column < GridCount; column++)
                {
                    double x = minX + width * (column + 0.5) / GridCount;
                    if (!IsInside(x, y, polygons))
                    {
                        continue;
                    }
                    double margin = DistanceToBoundary(x, y, polygons);
                    if (margin > bestMargin)
                    {
                        bestMargin = margin;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            if (bestMargin <= 0)
            {
                result.Note = "这些闭合曲线围出的区域里没有找到可点击的位置。";
                return result;
            }
            if (bestMargin < diagonal * 0.01)
            {
                result.Note = "封闭区域最宽处只有 " + bestMargin.ToString("0.###", CultureInfo.InvariantCulture)
                    + "，窄到不适合自动点选。";
                return result;
            }

            result.Found = true;
            result.Point = new Point3d(bestX, bestY, 0);
            result.Margin = bestMargin;
            // 箭头要够长才看得见，但不能长到跨过别的区域。
            result.ArrowLength = Math.Max(bestMargin * 1.6, diagonal * 0.04);
            return result;
        }

        private static Point2d[] Sample(Curve curve)
        {
            double start = curve.StartParam;
            double end = curve.EndParam;
            if (!(end > start))
            {
                return null;
            }
            int count = 320;
            Polyline polyline = curve as Polyline;
            if (polyline != null)
            {
                count = polyline.NumberOfVertices * 4;
                if (count < MinSamples) { count = MinSamples; }
                if (count > MaxSamples) { count = MaxSamples; }
            }
            Point2d[] points = new Point2d[count];
            for (int index = 0; index < count; index++)
            {
                double parameter = start + (end - start) * (index / (double)count);
                Point3d point = curve.GetPointAtParameter(parameter);
                points[index] = new Point2d(point.X, point.Y);
            }
            return points;
        }

        // 奇偶规则：射线穿过所有闭合曲线的次数为奇数即在区域内。
        // 这正好等价于填充命令默认的“普通”孤岛检测：外圈算在内，里面的圆算洞。
        private static bool IsInside(double x, double y, List<Point2d[]> polygons)
        {
            bool inside = false;
            for (int p = 0; p < polygons.Count; p++)
            {
                Point2d[] polygon = polygons[p];
                for (int index = 0; index < polygon.Length; index++)
                {
                    Point2d a = polygon[index];
                    Point2d b = polygon[index + 1 == polygon.Length ? 0 : index + 1];
                    if ((a.Y > y) == (b.Y > y))
                    {
                        continue;
                    }
                    double crossX = a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    if (crossX > x)
                    {
                        inside = !inside;
                    }
                }
            }
            return inside;
        }

        private static double DistanceToBoundary(double x, double y, List<Point2d[]> polygons)
        {
            double best = double.MaxValue;
            for (int p = 0; p < polygons.Count; p++)
            {
                Point2d[] polygon = polygons[p];
                for (int index = 0; index < polygon.Length; index++)
                {
                    Point2d a = polygon[index];
                    Point2d b = polygon[index + 1 == polygon.Length ? 0 : index + 1];
                    double distance = DistanceToSegment(x, y, a, b);
                    if (distance < best)
                    {
                        best = distance;
                    }
                }
            }
            return best == double.MaxValue ? 0 : best;
        }

        private static double DistanceToSegment(double x, double y, Point2d a, Point2d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0)
            {
                return Math.Sqrt((x - a.X) * (x - a.X) + (y - a.Y) * (y - a.Y));
            }
            double t = ((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared;
            if (t < 0) { t = 0; }
            if (t > 1) { t = 1; }
            double px = a.X + t * dx;
            double py = a.Y + t * dy;
            return Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
        }
    }
}
