using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;

namespace CombinePDF
{
    public partial class Form1 : Form
    {
        private List<string> filePaths = new List<string>(); // Store full paths

        public Form1()
        {
            InitializeComponent();
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "PDF Merger - Drag & Drop Files";
            this.Size = new Size(600, 500);
            this.AllowDrop = true;

            // ListView for files (supports drag-reorder)
            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                AllowDrop = true,
                MultiSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            lv.Columns.Add("File Name", 400);
            lv.Columns.Add("Path", 0); // Hidden column for full path
            lv.ItemDrag += Lv_ItemDrag;
            lv.DragEnter += Lv_DragEnter;
            lv.DragDrop += Lv_DragDrop;
            lv.DragOver += (s, e) => e.Effect = DragDropEffects.Move;

            // Drag & Drop for the whole form (fallback)
            this.DragEnter += Form_DragEnter;
            this.DragDrop += Form_DragDrop;

            // Buttons panel
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(10)
            };

            var btnMerge = new Button
            {
                Text = "Merge & Save...",
                Width = 150,
                Height = 35
            };
            btnMerge.Click += BtnMerge_Click;

            var btnClear = new Button
            {
                Text = "Clear List",
                Width = 100,
                Height = 35
            };
            btnClear.Click += (s, e) => { filePaths.Clear(); lv.Items.Clear(); };

            panel.Controls.Add(btnMerge);
            panel.Controls.Add(btnClear);

            this.Controls.Add(lv);
            this.Controls.Add(panel);

            // Store ListView reference
            lv.Tag = filePaths;
        }

        private void Form_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void Form_DragDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            AddFilesToList(files);
        }

        private void Lv_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(typeof(ListViewItem)))
                e.Effect = DragDropEffects.Copy | DragDropEffects.Move;
        }

        private void Lv_ItemDrag(object sender, ItemDragEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Item is ListViewItem lvi)
            {
                // Create drag data using the item directly (avoids the DataObject constructor overload ambiguity)
                DoDragDrop(lvi, DragDropEffects.Move);
            }
        }

        private void Lv_DragDrop(object sender, DragEventArgs e)
        {
            var lv = sender as ListView;
            if (lv == null) return;

            Point pt = lv.PointToClient(new Point(e.X, e.Y));
            ListViewItem targetItem = lv.GetItemAt(pt.X, pt.Y);

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                AddFilesToList(files);
            }
            else if (e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                var draggedItem = (ListViewItem)e.Data.GetData(typeof(ListViewItem));
                int targetIndex = targetItem != null ? targetItem.Index : lv.Items.Count;

                // Remove and re-insert to reorder
                lv.Items.Remove(draggedItem);
                lv.Items.Insert(targetIndex, draggedItem);
                // Update filePaths order
                filePaths.RemoveAt(draggedItem.Index);
                filePaths.Insert(targetIndex, filePaths[draggedItem.Index]);
            }
        }

        private void AddFilesToList(string[] files)
        {
            var lv = Controls[0] as ListView; // First control is ListView
            if (lv == null) return;

            foreach (var file in files)
            {
                if (File.Exists(file) &&
                    (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                     IsImageFile(file)))
                {
                    if (!filePaths.Contains(file))
                    {
                        filePaths.Add(file);
                        var item = new ListViewItem(Path.GetFileName(file));
                        item.SubItems.Add(file); // Hidden full path
                        lv.Items.Add(item);
                    }
                }
            }
        }

        private bool IsImageFile(string file)
        {
            var ext = Path.GetExtension(file).ToLower();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".gif";
        }

        private void BtnMerge_Click(object sender, EventArgs e)
        {
            if (filePaths.Count == 0)
            {
                MessageBox.Show("No files to merge!", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var sfd = new SaveFileDialog
            {
                Filter = "PDF Files (*.pdf)|*.pdf",
                Title = "Save Merged PDF",
                FileName = "MergedDocument.pdf"
            })
            {
                if (sfd.ShowDialog() != DialogResult.OK) return;

                try
                {
                    using var outputDoc = new PdfDocument();

                    foreach (var filePath in filePaths)
                    {
                        if (IsImageFile(filePath))
                        {
                            // Add image as a new page
                            var page = outputDoc.AddPage();
                            var gfx = XGraphics.FromPdfPage(page);
                            var image = XImage.FromFile(filePath);
                            gfx.DrawImage(image, 0, 0, page.Width, page.Height); // Scale to fit
                        }
                        else
                        {
                            // Merge existing PDF
                            using var inputDoc = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
                            foreach (PdfPage page in inputDoc.Pages)
                            {
                                outputDoc.AddPage(page);
                            }
                        }
                    }

                    outputDoc.Save(sfd.FileName);
                    MessageBox.Show($"Merged PDF saved successfully!\n{sfd.FileName}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error merging files:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
