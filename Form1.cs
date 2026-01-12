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
using PdfiumViewer;
using Image = System.Drawing.Image;

namespace CombinePDF
{
    public partial class Form1 : Form
    {
        private List<PageItem> finalPages = new List<PageItem>();   // Final pages to merge (source file + page index)
        private ListView lvSources;   // left side = source files
        private ListView lvFinal;     // right side = final pages
        private ImageList imageListSources;  // optional, if you want separate image lists
        private ImageList imageListFinal;
        private int dragSourceIndex = -1;

        public Form1()
        {
            InitializeComponent();
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "Advanced PDF Merger & Page Editor";
            this.Size = new Size(900, 700);  // Slightly narrower since no left panel
            this.AllowDrop = true;

            // No SplitContainer anymore - use a single main panel for the final view
            var mainPanel = new Panel { Dock = DockStyle.Fill };

            lvFinal = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.LargeIcon,
                LargeImageList = new ImageList { ImageSize = new Size(96, 128), ColorDepth = ColorDepth.Depth32Bit },
                MultiSelect = true,
                AllowDrop = true,
                LabelEdit = false
            };

            //lvFinal.AutoArrange = false;          // Important: prevents auto-snap interfering
            lvFinal.Sorting = SortOrder.None;     // No auto-sorting
            lvFinal.View = View.LargeIcon;        // Confirm this (insertion mark works best here)

            // Drag & drop reorder + add from files
            //lvFinal.ItemDrag += (s, e) => { if (e.Button == MouseButtons.Left) DoDragDrop(e.Item, DragDropEffects.Move); };
            //lvFinal.DragEnter += (s, e) => e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
            //lvFinal.DragDrop += (s, e) =>
            //{
            //    Point pt = lvFinal.PointToClient(new Point(e.X, e.Y));
            //    var target = lvFinal.GetItemAt(pt.X, pt.Y);

            //    if (e.Data.GetDataPresent(typeof(ListViewItem))) // Reorder
            //    {
            //        var dragged = (ListViewItem)e.Data.GetData(typeof(ListViewItem));
            //        int idx = target?.Index ?? lvFinal.Items.Count;
            //        lvFinal.Items.Remove(dragged);
            //        lvFinal.Items.Insert(idx, dragged);
            //        var page = (PageItem)dragged.Tag;
            //        finalPages.Remove(page);
            //        finalPages.Insert(idx, page);
            //    }
            //    else if (e.Data.GetDataPresent(DataFormats.FileDrop)) // Add new file(s)
            //    {
            //        AddToFinalFromFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            //    }
            //};

            lvFinal.AllowDrop = true;
            lvFinal.ItemDrag += lvFinal_ItemDrag;          // rename if needed
            lvFinal.DragEnter += lvFinal_DragEnter;
            lvFinal.DragOver += lvFinal_DragOver;          // ← new: for insertion mark
            lvFinal.DragLeave += lvFinal_DragLeave;        // ← new: clean up
            lvFinal.DragDrop += lvFinal_DragDrop;          // updated version
            lvFinal.InsertionMark.Color = Color.DodgerBlue;
            //lvFinal.AutoArrange = false;

            imageListFinal = lvFinal.LargeImageList;
            mainPanel.Controls.Add(lvFinal);

            // Bottom buttons panel
            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(10),
                BackColor = Color.LightGray  // Optional: slight visual separation
            };

            var btnAddFiles = new Button { Text = "Add Files...", Width = 120 };
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

            var btnDelete = new Button { Text = "Delete Selected", Width = 120 };
            btnDelete.Click += (s, e) =>
            {
                foreach (ListViewItem item in lvFinal.SelectedItems.Cast<ListViewItem>().ToList())
                {
                    finalPages.Remove((PageItem)item.Tag);
                    lvFinal.Items.Remove(item);
                }
            };

            var btnMergeSave = new Button { Text = "Merge & Save...", Width = 140 };
            btnMergeSave.Click += BtnMergeSave_Click;

            btnPanel.Controls.Add(btnAddFiles);
            btnPanel.Controls.Add(btnDelete);
            btnPanel.Controls.Add(btnMergeSave);

            mainPanel.Controls.Add(btnPanel);

            this.Controls.Add(mainPanel);

            // Form-level drag & drop (fallback + main way to add files now)
            //this.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            //this.DragDrop += (s, e) => AddToFinalFromFiles((string[])e.Data.GetData(DataFormats.FileDrop));

            // Optional: tag for clarity (not strictly needed anymore)
            lvFinal.Tag = "final";
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

        // replace existing lvFinal_DragOver with this (ensures Effect is set and insertion mark logic stays)
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

        // replace existing lvFinal_DragDrop with this (robust move handling and correct index adjustments)
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
                lvFinal.InsertionMark.Index = -1;
                dragSourceIndex = -1;
            }
        }

        private void AddSourceFiles(string[] files)
        {            
            var imgList = lvSources.LargeImageList;

            foreach (var file in files.Where(File.Exists))
            {
                if (!IsSupported(file)) continue;

                var item = new ListViewItem(Path.GetFileName(file));
                item.Tag = file;

                // Generate thumbnail
                var bmp = GenerateThumbnail(file, 96, 128);
                if (bmp != null)
                {
                    imgList.Images.Add(file, bmp);
                    item.ImageKey = file;
                }

                lvSources.Items.Add(item);
            }
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
                MessageBox.Show("No pages in final document!", "Info");
                return;
            }

            using var sfd = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = "Merged.pdf" };
            if (sfd.ShowDialog() != DialogResult.OK) return;

            try
            {
                using var output = new PdfSharpCore.Pdf.PdfDocument();

                foreach (var item in finalPages)
                {
                    if (item.PageIndex == -1) // Image
                    {
                        var page = output.AddPage();
                        var gfx = XGraphics.FromPdfPage(page);
                        var ximg = XImage.FromFile(item.SourceFile);
                        gfx.DrawImage(ximg, 0, 0, page.Width, page.Height); // fit
                    }
                    else // PDF page
                    {
                        using var srcDoc = PdfReader.Open(item.SourceFile, PdfDocumentOpenMode.Import);
                        output.AddPage(srcDoc.Pages[item.PageIndex]);
                    }
                }

                output.Save(sfd.FileName);
                MessageBox.Show("Saved successfully!", "Done");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error:\n{ex.Message}", "Failed");
            }
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
