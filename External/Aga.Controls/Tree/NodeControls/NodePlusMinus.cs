using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using Aga.Controls.Properties;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace Aga.Controls.Tree.NodeControls
{
	internal class NodePlusMinus : NodeControl
	{
		public const int ImageSize = 9;
		public const int Width = 16;
		private Bitmap _plus;
		private Bitmap _minus;

		private VisualStyleRenderer _openedRenderer;
		private VisualStyleRenderer OpenedRenderer
		{
			get
			{
				if (_openedRenderer == null)
					_openedRenderer = new VisualStyleRenderer(VisualStyleElement.TreeView.Glyph.Opened);
				return _openedRenderer;

			}
		}

		private VisualStyleRenderer _closedRenderer;
		private VisualStyleRenderer ClosedRenderer
		{
			get
			{
				if (_closedRenderer == null)
					_closedRenderer = new VisualStyleRenderer(VisualStyleElement.TreeView.Glyph.Closed);
				return _closedRenderer;
			}
		}

		public NodePlusMinus()
		{
			_plus = Resources.plus;
			_minus = Resources.minus;
		}

		public override Size MeasureSize(TreeNodeAdv node, DrawContext context)
		{
			int scaledX = node.Tree.GetScaledSize(Width, false);
			int scaledY = node.Tree.GetScaledSize(Width);
			return new Size(scaledX, scaledY);
		}

		public override void Draw(TreeNodeAdv node, DrawContext context)
		{
			if (node.CanExpand)
			{
				Rectangle r = context.Bounds;
				int scaledX = node.Tree.GetScaledSize(ImageSize, false);
				int scaledY = node.Tree.GetScaledSize(ImageSize);
				int dy = (int)Math.Round((float)(r.Height - scaledY) / 2);
				if (Application.RenderWithVisualStyles)
				{
					// A flat chevron in a muted text colour instead of the boxed themed
					// glyph: quieter, and crisp at any DPI.
					Color treeBack = node.Tree.BackColor, treeFore = node.Tree.ForeColor;
					Color chevronColor = Color.FromArgb(
						(treeBack.R * 45 + treeFore.R * 55) / 100,
						(treeBack.G * 45 + treeFore.G * 55) / 100,
						(treeBack.B * 45 + treeFore.B * 55) / 100);
					float size = scaledY * 0.8f;
					float cx = r.X + scaledX / 2f;
					float cy = r.Y + r.Height / 2f;
					PointF[] points = node.IsExpanded
						? new[] { new PointF(cx - size / 2, cy - size / 4), new PointF(cx, cy + size / 4), new PointF(cx + size / 2, cy - size / 4) }
						: new[] { new PointF(cx - size / 4, cy - size / 2), new PointF(cx + size / 4, cy), new PointF(cx - size / 4, cy + size / 2) };
					System.Drawing.Drawing2D.SmoothingMode previousSmoothing = context.Graphics.SmoothingMode;
					context.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
					using (Pen pen = new Pen(chevronColor, Math.Max(1.5f, scaledY / 6f)))
					{
						pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
						pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
						pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
						context.Graphics.DrawLines(pen, points);
					}
					context.Graphics.SmoothingMode = previousSmoothing;
				}
				else
				{
					Image img;
					if (node.IsExpanded)
						img = _minus;
					else
						img = _plus;
					context.Graphics.DrawImageUnscaled(img, new Point(r.X, r.Y + dy));
				}
			}
		}

		public override void MouseDown(TreeNodeAdvMouseEventArgs args)
		{
			if (args.Button == MouseButtons.Left)
			{
				args.Handled = true;
				if (args.Node.CanExpand)
					args.Node.IsExpanded = !args.Node.IsExpanded;
			}
		}

		public override void MouseDoubleClick(TreeNodeAdvMouseEventArgs args)
		{
			args.Handled = true; // Supress expand/collapse when double click on plus/minus
		}
	}
}
