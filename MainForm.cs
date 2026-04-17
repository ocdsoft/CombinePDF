using PdfiumViewer;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Image = System.Drawing.Image;
using TrackBar = System.Windows.Forms.TrackBar;

namespace CombinePDF
{
    public partial class MainForm : Form
    {
        private TrackBar qualitySlider;
        private Label lblQuality;
        private int currentDpi = 150;        // Default: good balance (was 300 before)
        private List<PageItem> finalPages = new List<PageItem>();   // Final pages to merge (source file + page index)
        private System.Windows.Forms.ListView lvFinal;     // right side = final pages
        private ImageList imageListFinal;
        private int dragSourceIndex = -1;
        private Label labelSaveMessage;
        private FlowLayoutPanel btnPanel;
        private System.Windows.Forms.ToolTip toolTipSaveMessage;  // field at class level

        public MainForm()
        {
            InitializeComponent();
            SetupUI();
        }

        
        private void ScaleThumbnails()
        {
            using var g = this.CreateGraphics();
            float dpiX = g.DpiX;  // Current DPI

            // Scale from base 96 DPI
            float scale = dpiX / 96f;

            int baseWidth = 96;
            int baseHeight = 128;

            int newWidth = (int)(baseWidth * scale);
            int newHeight = (int)(baseHeight * scale);

            // Minimum size to avoid tiny thumbs
            newWidth = Math.Max(64, newWidth);
            newHeight = Math.Max(85, newHeight);

            lvFinal.LargeImageList.ImageSize = new Size(newWidth, newHeight);

            // Optional: regenerate thumbnails at new size (best quality)
            // This requires re-adding images — do only if DPI changed significantly
            // For simplicity, rely on bitmap scaling (WinForms handles it ok)
        }

        private void ShowPagePreview(ListViewItem item)
        {
            if (item?.Tag is not PageItem pageItem)
                return;

            var previewForm = new Form
            {
                Text = item.Text + " - Preview",
                WindowState = FormWindowState.Maximized,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimumSize = new Size(900, 700),
                BackColor = Color.Black
            };

            // Escape key to close
            previewForm.KeyPreview = true;
            previewForm.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                    previewForm.Close();
            };

            try
            {
                if (pageItem.PageIndex == -1) // Image file
                {
                    using var original = Image.FromFile(pageItem.SourceFile);
                    var pb = new PictureBox
                    {
                        Dock = DockStyle.Fill,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Image = new Bitmap(original),   // copy to avoid file lock
                        BackColor = Color.Black
                    };
                    previewForm.Controls.Add(pb);
                }
                else // PDF page - render ONLY this page at high resolution
                {
                    var bmp = RenderPageToBitmapHighQuality(pageItem.SourceFile, pageItem.PageIndex);

                    if (bmp == null)
                    {
                        MessageBox.Show("Failed to render the selected PDF page.", "Preview Error",
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var pb = new PictureBox
                    {
                        Dock = DockStyle.Fill,
                        SizeMode = PictureBoxSizeMode.Zoom,   // or .CenterImage if you prefer exact size
                        Image = bmp,
                        BackColor = Color.White
                    };
                    previewForm.Controls.Add(pb);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load preview:\n{ex.Message}", "Preview Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            previewForm.ShowDialog(this);
        }

        private Bitmap RenderPageToBitmapHighQuality(string pdfPath, int pageIndex)
        {
            try
            {
                using var document = PdfiumViewer.PdfDocument.Load(pdfPath);

                if (pageIndex < 0 || pageIndex >= document.PageCount)
                    return null;

                var pageSize = document.PageSizes[pageIndex];

                // Higher DPI for much better preview quality (adjust as needed)
                const int dpi = 300;

                int renderWidth = (int)(pageSize.Width * dpi / 72.0);
                int renderHeight = (int)(pageSize.Height * dpi / 72.0);

                using var fullBitmap = document.Render(pageIndex, renderWidth, renderHeight, dpi, dpi, false);

                // Return a copy so we can dispose the document safely
                return new Bitmap(fullBitmap);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"High-quality render failed: {ex.Message}");
                return null;
            }
        }

        private Bitmap RenderPageToBitmapWithDpi(string pdfPath, int pageIndex, int dpi)
        {
            try
            {
                using var document = PdfiumViewer.PdfDocument.Load(pdfPath);

                if (pageIndex < 0 || pageIndex >= document.PageCount)
                    return null;

                var pageSize = document.PageSizes[pageIndex];

                int renderWidth = (int)(pageSize.Width * dpi / 72.0);
                int renderHeight = (int)(pageSize.Height * dpi / 72.0);

                using var fullBitmap = document.Render(pageIndex, renderWidth, renderHeight, dpi, dpi, false);

                return new Bitmap(fullBitmap);   // copy so Pdfium document can be disposed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Render at {dpi} DPI failed: {ex.Message}");
                return null;
            }
        }

        private void SetupUI()
        {
            this.Text = "Combine PDF";
            this.Size = new Size(900, 700);  // Slightly narrower since no left panel
            this.AllowDrop = true;
            this.AutoScaleMode = AutoScaleMode.Dpi;  // or AutoScaleMode.Font if font scaling matters more
            this.AutoScaleDimensions = new SizeF(96F, 96F);  // Design-time 96 DPI baseline

            // No SplitContainer anymore - use a single main panel for the final view
            var mainPanel = new Panel { Dock = DockStyle.Fill };

            toolTipSaveMessage = new System.Windows.Forms.ToolTip
            {
                AutoPopDelay = 30000,   // show longer
                InitialDelay = 500,
                ShowAlways = true
            };

            lvFinal = new System.Windows.Forms.ListView
            {
                Dock = DockStyle.Fill,
                View = View.LargeIcon,
                LargeImageList = new ImageList { ImageSize = new Size(96, 128), ColorDepth = ColorDepth.Depth32Bit },
                MultiSelect = true,
                AllowDrop = true,
                LabelEdit = false
            };

            lvFinal.AutoArrange = true;          // Important: prevents auto-snap interfering
            lvFinal.Sorting = SortOrder.None;     // No auto-sorting
            lvFinal.View = View.LargeIcon;        // Confirm this (insertion mark works best here)
            lvFinal.Alignment = ListViewAlignment.Default;         
            
            lvFinal.ItemDrag += lvFinal_ItemDrag;          // rename if needed
            lvFinal.DragEnter += lvFinal_DragEnter;
            lvFinal.DragOver += lvFinal_DragOver;          // ← new: for insertion mark
            lvFinal.DragLeave += lvFinal_DragLeave;        // ← new: clean up
            lvFinal.DragDrop += lvFinal_DragDrop;          // updated version
            lvFinal.InsertionMark.Color = Color.DodgerBlue;
            // Add this line:
            lvFinal.ItemActivate += lvFinal_ItemActivate;   // Recommended (double-click in LargeIcon view)

            imageListFinal = lvFinal.LargeImageList;
            mainPanel.Controls.Add(lvFinal);

            // Bottom buttons panel
            btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 80,
                Padding = new Padding(10),
                BackColor = Color.LightGray,  // Optional: slight visual separation
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,  // Prevent wrapping on narrow windows
                AutoSize = true,       // Let it grow vertically if needed
                AutoScroll = true,               
                Margin = new Padding(0, 0, 0, 10)
            };
                        
            btnPanel.AutoScrollMargin = new Size(0, 20);

            btnPanel.Scroll += btnPanel_Scroll;

            var btnAddFiles = new System.Windows.Forms.Button { Text = "Add Files...", Width = 120 };
            btnAddFiles.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Multiselect = true,
                    Filter = "PDF & Images|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif"
                };
                if (ofd.ShowDialog() == DialogResult.OK)
                    AddToFinalFromFiles(ofd.FileNames);  // Add directly to final
            };

            var btnDelete = new System.Windows.Forms.Button { Text = "Delete Selected", Width = 120 };
            btnDelete.Click += (s, e) =>
            {
                foreach (ListViewItem item in lvFinal.SelectedItems.Cast<ListViewItem>().ToList())
                {
                    finalPages.Remove((PageItem)item.Tag);
                    lvFinal.Items.Remove(item);
                }
            };

            var btnMergeSave = new System.Windows.Forms.Button { Text = "Merge & Save...", Width = 140 };
            btnMergeSave.Click += BtnMergeSave_Click;

            btnAddFiles.AutoSize = true;
            btnDelete.AutoSize = true;
            btnMergeSave.AutoSize = true;

            labelSaveMessage = new Label
            {
                Text = "",
                AutoSize = true,
                MinimumSize = new Size(300, 0),           // Prevents collapsing too small
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Black,
                Visible = false,                           // Start hidden
                Padding = new Padding(5, 5, 10, 5),      // Breathing room
                Margin = new Padding(0, 0, 0, 0)  // Space from previous buttons                
                
            };

            // New: Quality controls for PDF rendering resolution
            lblQuality = new Label
            {
                Text = "PDF Render DPI:",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 8, 0, 0)
            };

            qualitySlider = new TrackBar
            {
                Minimum = 72,      // Very small / low quality
                Maximum = 300,     // High quality
                Value = currentDpi,
                TickFrequency = 30,
                SmallChange = 10,
                LargeChange = 50,
                Width = 200,
                Height = 30,
                AutoSize = false
            };

            qualitySlider.ValueChanged += (s, e) =>
            {
                currentDpi = qualitySlider.Value;
                lblQuality.Text = $"PDF Render DPI: {currentDpi}";
                toolTipSaveMessage.SetToolTip(qualitySlider,
                    $"Render PDF pages at {currentDpi} DPI\n" +
                    "Lower = smaller file size\nHigher = better quality");
            };

            btnPanel.Controls.Add(btnAddFiles);
            btnPanel.Controls.Add(btnDelete);
            btnPanel.Controls.Add(btnMergeSave);
            btnPanel.Controls.Add(lblQuality);
            btnPanel.Controls.Add(qualitySlider);
            btnPanel.Controls.Add(labelSaveMessage);

            mainPanel.Controls.Add(btnPanel);

            this.Controls.Add(mainPanel);            

            // Optional: tag for clarity (not strictly needed anymore)
            lvFinal.Tag = "final";

            ScaleThumbnails();

            this.Resize += (s, e) =>
            {
                if (this.WindowState == FormWindowState.Normal)
                {
                    // Optional: adjust thumbnail size on resize if desired
                    ScaleThumbnails();
                }
            };
        }
        private void btnPanel_Scroll(object sender, ScrollEventArgs e)
        {
            // Called when scroll happens / scrollbar appears
            if (btnPanel.VerticalScroll.Visible)
            {
                // Add extra bottom padding when scrollbar is visible
                btnPanel.Padding = new Padding(10, 10, 10, 30);  // bottom 30px to clear scrollbar
            }
            else
            {
                btnPanel.Padding = new Padding(10);  // normal padding
            }
        }

        private void lvFinal_ItemDrag(object sender, ItemDragEventArgs e)
        {
            // Store source index for a reliable reference during Drop
            if (e.Item is ListViewItem item)
            {
                dragSourceIndex = item.Index;
                DoDragDrop(e.Item, DragDropEffects.Move);
            }
        }

        private void lvFinal_DragEnter(object sender, DragEventArgs e)
        {
            // Allow our own reordering (Move) + file drops (Copy)
            if (e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                e.Effect = DragDropEffects.Move;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }
        
        private void lvFinal_DragOver(object sender, DragEventArgs e)
        {
            // Ensure an appropriate effect is reported
            if (e.Data.GetDataPresent(typeof(ListViewItem)))
                e.Effect = DragDropEffects.Move;
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;

            // Only update insertion mark when moving ListView items
            if (e.Effect != DragDropEffects.Move)
                return;

            Point pt = lvFinal.PointToClient(new Point(e.X, e.Y));
            ListViewItem targetItem = lvFinal.GetItemAt(pt.X, pt.Y);

            if (targetItem == null)
            {
                lvFinal.InsertionMark.Index = -1;
            }
            else
            {
                Rectangle bounds = targetItem.Bounds;
                int midpointY = bounds.Top + (bounds.Height / 2);

                if (pt.Y < midpointY)
                {
                    lvFinal.InsertionMark.Index = targetItem.Index;
                    lvFinal.InsertionMark.AppearsAfterItem = false;
                }
                else
                {
                    lvFinal.InsertionMark.Index = targetItem.Index;
                    lvFinal.InsertionMark.AppearsAfterItem = true;
                }
            }
        }

        private void lvFinal_DragLeave(object sender, EventArgs e)
        {
            // Clean up the insertion mark when mouse leaves the control
            lvFinal.InsertionMark.Index = -1;
        }

        private void lvFinal_DragDrop(object sender, DragEventArgs e)
        {
            // Handle file drop
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                AddToFinalFromFiles((string[])e.Data.GetData(DataFormats.FileDrop));
                lvFinal.InsertionMark.Index = -1;
                dragSourceIndex = -1;
                return;
            }

            if (!e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                dragSourceIndex = -1;
                return;
            }

            // Resolve the dragged item and source index
            var draggedItemFromData = (ListViewItem)e.Data.GetData(typeof(ListViewItem));
            int sourceIndex = dragSourceIndex >= 0 ? dragSourceIndex : draggedItemFromData?.Index ?? -1;
            if (sourceIndex < 0 || sourceIndex >= lvFinal.Items.Count)
            {
                // Fallback: if the stored index is invalid, try to locate the item by reference
                sourceIndex = Array.IndexOf(lvFinal.Items.Cast<ListViewItem>().ToArray(), draggedItemFromData);
                if (sourceIndex < 0)
                {
                    dragSourceIndex = -1;
                    return;
                }
            }

            int targetIndex = lvFinal.InsertionMark.Index;
            if (targetIndex == -1)
            {
                // append to end
                targetIndex = lvFinal.Items.Count - 1;
                targetIndex++; // insert at Count (append)
            }
            else if (lvFinal.InsertionMark.AppearsAfterItem)
            {
                targetIndex++;
            }

            // No-op checks (drop at same place or adjacent no-op)
            if (targetIndex == sourceIndex || targetIndex == sourceIndex + 1)
            {
                lvFinal.InsertionMark.Index = -1;
                dragSourceIndex = -1;
                return;
            }

            lvFinal.BeginUpdate();
            try
            {
                // Use the actual existing ListViewItem instance for smooth visual behavior
                var itemToMove = lvFinal.Items[sourceIndex];

                // Remove from UI and insert at new index (adjust target if necessary)
                lvFinal.Items.RemoveAt(sourceIndex);
                if (sourceIndex < targetIndex) targetIndex--; // removal shifts indexes down
                lvFinal.Items.Insert(targetIndex, itemToMove);

                // Sync backing list
                var page = (PageItem)itemToMove.Tag;
                // Remove at original source (adjusted using original sourceIndex)
                finalPages.RemoveAt(sourceIndex);
                // Insert at adjusted target index
                finalPages.Insert(targetIndex, page);

                // Select and ensure visible the moved item
                itemToMove.Selected = true;
                itemToMove.Focused = true;
                lvFinal.EnsureVisible(targetIndex);
            }
            finally
            {
                lvFinal.EndUpdate();
                ForceListViewLayoutRefresh(lvFinal);
                lvFinal.InsertionMark.Index = -1;
                dragSourceIndex = -1;
            }
        }

        private void lvFinal_ItemActivate(object sender, EventArgs e)
        {
            if (lvFinal.SelectedItems.Count == 1)
            {
                ShowPagePreview(lvFinal.SelectedItems[0]);
            }
        }
        private void ForceListViewLayoutRefresh(System.Windows.Forms.ListView listView)
        {
            var originalView = listView.View;
            listView.View = View.Tile;      // or View.Details — whichever flickers least noticeably
            listView.View = originalView;
            listView.Refresh();
            listView.Update();
        }

        private void AddToFinalFromFiles(string[] files)
        {
            var imgList = lvFinal.LargeImageList;

            foreach (var file in files.Where(File.Exists).Where(IsSupported))
            {
                if (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = PdfReader.Open(file, PdfDocumentOpenMode.Import);

                        int pageCount = doc.PageCount;

                        // Optional: handle truly empty PDFs gracefully
                        if (pageCount == 0)
                        {
                            MessageBox.Show($"The PDF file is empty (0 pages):\n{Path.GetFileName(file)}",
                                            "Empty PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            continue;
                        }

                        for (int i = 0; i < pageCount; i++)
                        {
                            var pageItem = new PageItem { SourceFile = file, PageIndex = i };
                            finalPages.Add(pageItem);

                            var bmp = RenderPageToBitmap(file, i, 96, 128);
                            if (bmp != null)
                            {
                                string key = $"{file}|{i}";
                                imgList.Images.Add(key, bmp);

                                var lvi = new ListViewItem($"Pg {i + 1} - {Path.GetFileName(file)}")
                                {
                                    Tag = pageItem,
                                    ImageKey = key
                                };
                                lvFinal.Items.Add(lvi);
                            }
                            else
                            {
                                // Fallback thumbnail if rendering fails
                                var fallbackBmp = CreateFallbackThumbnail(file, i);
                                if (fallbackBmp != null)
                                {
                                    string fallbackKey = $"fallback|{file}|{i}";
                                    imgList.Images.Add(fallbackKey, fallbackBmp);
                                    var lvi = new ListViewItem($"Pg {i + 1} - {Path.GetFileName(file)} (preview failed)")
                                    {
                                        Tag = pageItem,
                                        ImageKey = fallbackKey
                                    };
                                    lvFinal.Items.Add(lvi);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to load PDF:\n{Path.GetFileName(file)}\n\n{ex.Message}",
                                        "PDF Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        // Continue with next file instead of crashing
                    }
                }
                else // image files
                {
                    try
                    {
                        var pageItem = new PageItem { SourceFile = file, PageIndex = -1 }; // -1 = image
                        finalPages.Add(pageItem);

                        var bmp = GenerateThumbnail(file, 96, 128);
                        if (bmp != null)
                        {
                            string key = file;
                            imgList.Images.Add(key, bmp);

                            var lvi = new ListViewItem(Path.GetFileName(file))
                            {
                                Tag = pageItem,
                                ImageKey = key
                            };
                            lvFinal.Items.Add(lvi);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to load image:\n{Path.GetFileName(file)}\n\n{ex.Message}",
                                        "Image Load Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }

            // Optional: refresh the list view after adding
            lvFinal.Refresh();
        }

        private Bitmap GenerateThumbnail(string filePath, int width, int height)
        {
            try
            {
                if (IsImageFile(filePath))
                {
                    using var img = Image.FromFile(filePath);
                    return new Bitmap(img, new Size(width, height));
                }

                // For PDFs: render page 0
                return RenderPageToBitmap(filePath, 0, width, height);
            }
            catch
            {
                return null;  // fallback placeholder
            }
        }

        private Bitmap RenderPageToBitmap(string pdfPath, int pageIndex, int thumbWidth, int thumbHeight)
        {
            try
            {
                using var document = PdfiumViewer.PdfDocument.Load(pdfPath);

                if (pageIndex < 0 || pageIndex >= document.PageCount)
                    return null;

                // Get page size in points (1 point = 1/72 inch)
                var pageSize = document.PageSizes[pageIndex];
                int dpi = 120;
                int renderWidth = (int)(pageSize.Width * dpi / 72.0);
                int renderHeight = (int)(pageSize.Height * dpi / 72.0);

                // Render the page to an image
                using var fullBitmap = document.Render(pageIndex, renderWidth, renderHeight, dpi, dpi, false);

                // Scale down to thumbnail size while preserving aspect ratio
                return new Bitmap(fullBitmap, new Size(thumbWidth, thumbHeight));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Render failed for {pdfPath} page {pageIndex}: {ex.Message}");
                return null;
            }
        }

        private Bitmap CreateFallbackThumbnail(string filePath, int pageIndex)
        {
            try
            {
                var bmp = new Bitmap(96, 128, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.WhiteSmoke);
                using var font = new System.Drawing.Font("Segoe UI", 9, FontStyle.Regular);
                using var brush = new SolidBrush(Color.DimGray);

                g.DrawString("PDF Page", font, brush, 10, 30);
                g.DrawString($"Page {pageIndex + 1}", font, brush, 10, 55);
                g.DrawString(Path.GetFileName(filePath), font, brush, 10, 80);

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void BtnMergeSave_Click(object sender, EventArgs e)
        {
            if (!finalPages.Any())
            {
                labelSaveMessage.Text = "No pages to merge!";
                labelSaveMessage.ForeColor = Color.Black;
                labelSaveMessage.Visible = true;
                btnPanel.PerformLayout();
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Filter = "PDF|*.pdf",
                FileName = "Merged.pdf"
            };

            if (sfd.ShowDialog() != DialogResult.OK)
                return;

            try
            {
                using var output = new PdfSharpCore.Pdf.PdfDocument();

                foreach (var item in finalPages)
                {
                    if (item.PageIndex == -1) // Image file - unchanged (uses JPEG quality)
                    {
                        var page = output.AddPage();
                        var gfx = XGraphics.FromPdfPage(page);

                        using var sysImage = Image.FromFile(item.SourceFile);

                        var encoder = GetJpegEncoder();
                        var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, long.Parse(((currentDpi / 300.0) * 100).ToString("#,##0"))); // fixed good default for images

                        using var ms = new MemoryStream();
                        sysImage.Save(ms, encoder, encoderParams);
                        ms.Position = 0;

                        using var ximg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                        gfx.DrawImage(ximg, 0, 0, page.Width, page.Height);
                    }
                    else // PDF page → render at selected DPI and embed as image
                    {
                        var bmp = RenderPageToBitmapWithDpi(item.SourceFile, item.PageIndex, currentDpi);

                        if (bmp == null)
                        {
                            // Fallback: just copy the original page (no quality reduction)
                            using var srcDoc = PdfReader.Open(item.SourceFile, PdfDocumentOpenMode.Import);
                            output.AddPage(srcDoc.Pages[item.PageIndex]);
                            continue;
                        }

                        var page = output.AddPage();
                        var gfx = XGraphics.FromPdfPage(page);

                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Jpeg);   // or use quality parameter if you want
                        ms.Position = 0;

                        using var ximg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                        gfx.DrawImage(ximg, 0, 0, page.Width, page.Height);
                    }
                }

                output.Save(sfd.FileName);

                labelSaveMessage.Text = $"Saved successfully: {sfd.FileName} (PDF pages rendered at {currentDpi} DPI)";
                labelSaveMessage.ForeColor = Color.Green;
                labelSaveMessage.Visible = true;
            }
            catch (Exception ex)
            {
                labelSaveMessage.Text = $"Failed to save the PDF. Error: {ex.Message}";
                labelSaveMessage.ForeColor = Color.Red;
                labelSaveMessage.Visible = true;
            }

            btnPanel.PerformLayout();
            btnPanel.Refresh();
            toolTipSaveMessage.SetToolTip(labelSaveMessage, labelSaveMessage.Text);
        }

        private ImageCodecInfo GetJpegEncoder()
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                    return codec;
            }
            throw new Exception("JPEG Encoder not found.");
        }

        private bool IsSupported(string file) =>
            IsImageFile(file) || Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

        private bool IsImageFile(string file)
        {
            var ext = Path.GetExtension(file)?.ToLower();
            return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif";
        }
    }

    public class PageItem
    {
        public string SourceFile { get; set; }
        public int PageIndex { get; set; }   // -1 for image
    }
}
