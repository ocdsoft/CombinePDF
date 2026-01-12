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

            // Drag & drop reorder + add from files
            lvFinal.ItemDrag += (s, e) => { if (e.Button == MouseButtons.Left) DoDragDrop(e.Item, DragDropEffects.Move); };
            lvFinal.DragEnter += (s, e) => e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
            lvFinal.DragDrop += (s, e) =>
            {
                Point pt = lvFinal.PointToClient(new Point(e.X, e.Y));
                var target = lvFinal.GetItemAt(pt.X, pt.Y);

                if (e.Data.GetDataPresent(typeof(ListViewItem))) // Reorder
                {
                    var dragged = (ListViewItem)e.Data.GetData(typeof(ListViewItem));
                    int idx = target?.Index ?? lvFinal.Items.Count;
                    lvFinal.Items.Remove(dragged);
                    lvFinal.Items.Insert(idx, dragged);
                    var page = (PageItem)dragged.Tag;
                    finalPages.Remove(page);
                    finalPages.Insert(idx, page);
                }
                else if (e.Data.GetDataPresent(DataFormats.FileDrop)) // Add new file(s)
                {
                    AddToFinalFromFiles((string[])e.Data.GetData(DataFormats.FileDrop));
                }
            };

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
            this.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            this.DragDrop += (s, e) => AddToFinalFromFiles((string[])e.Data.GetData(DataFormats.FileDrop));

            // Optional: tag for clarity (not strictly needed anymore)
            lvFinal.Tag = "final";
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
