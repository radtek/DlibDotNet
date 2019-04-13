﻿/*
 * This sample program is ported by C# from examples\dnn_semantic_segmentation_train_ex.cpp.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DlibDotNet;
using DlibDotNet.Dnn;
using DlibDotNet.Extensions;
using DnnSemanticSegmentation;

namespace DnnSemanticSegmentationTrainOld
{

    internal class Program
    {

        #region Constructors

        static Program()
        {
            ContainerBridgeRepository.Add(new TrainingSampleContainerBridge());
        }

        #endregion

        #region Methods

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1)
                {
                    Console.WriteLine("To run this program you need a copy of the PASCAL VOC2012 dataset.");
                    Console.WriteLine();
                    Console.WriteLine("You call this program like this: ");
                    Console.WriteLine("./dnn_semantic_segmentation_train_ex /path/to/VOC2012");
                    return 1;
                }

                Console.WriteLine("\nSCANNING PASCAL VOC2012 DATASET\n");

                var listing = GetPascalVoc2012TrainListing(args[0]).ToArray();
                Console.WriteLine($"images in dataset: {listing.Length}");
                if (listing.Length == 0)
                {
                    Console.WriteLine("Didn't find the VOC2012 dataset.");
                    return 1;
                }

                const double initialLearningRate = 0.1;
                const double weightDecay = 0.0001;
                const double momentum = 0.9;

                using (var net = new LossMulticlassLogPerPixel(1))
                using (var sgd = new Sgd((float)weightDecay, (float)momentum))
                using (var trainer = new DnnTrainer<LossMulticlassLogPerPixel>(net, sgd))
                {
                    trainer.BeVerbose();
                    trainer.SetLearningRate(initialLearningRate);
                    trainer.SetSynchronizationFile("pascal_voc2012_trainer_state_file.dat", 10 * 60);
                    // This threshold is probably excessively large.
                    trainer.SetIterationsWithoutProgressThreshold(5000);
                    // Since the progress threshold is so large might as well set the batch normalization
                    // stats window to something big too.
                    Dlib.SetAllBnRunningStatsWindowSizes(net, 1000);

                    // Output training parameters.
                    Console.WriteLine();
                    Console.WriteLine(trainer);

                    var samples = new List<Matrix<RgbPixel>>();
                    var labels = new List<Matrix<ushort>>();

                    //// Start a bunch of threads that read images from disk and pull out random crops.  It's
                    //// important to be sure to feed the GPU fast enough to keep it busy.  Using multiple
                    //// thread for this kind of data preparation helps us do that.  Each thread puts the
                    //// crops into the data queue.
                    using (var data = new Pipe<TrainingSample>(200))
                    {
                        var function = new Action<object>(seed =>
                        {
                            using (var rnd = new Rand((ulong)seed))
                            {
                                while (data.IsEnabled)
                                {
                                    // Pick a random input image.
                                    var imageInfo = listing[rnd.GetRandom32BitNumber() % listing.Length];

                                    // Load the input image.
                                    using (var inputImage = Dlib.LoadImageAsMatrix<RgbPixel>(imageInfo.ImageFilename))
                                    {
                                        // Load the ground-truth (RGB) labels.
                                        using (var rgbLabelImage = Dlib.LoadImageAsMatrix<RgbPixel>(imageInfo.LabelFilename))
                                        {
                                            // Convert the indexes to RGB values.
                                            using (var indexLabelImage = new Matrix<ushort>())
                                            {
                                                RgbLabelImageToIndexLabelImage(rgbLabelImage, indexLabelImage);

                                                // Randomly pick a part of the image.
                                                var temp = new TrainingSample();
                                                RandomlyCropImage(inputImage, indexLabelImage, temp, rnd);

                                                // Push the result to be used by the trainer.
                                                data.Enqueue(temp);
                                            }
                                        }
                                    }
                                }
                            }
                        });

                        var threads = Enumerable.Range(1, 1).Select(i =>
                        {
                            var dataLoader = new Thread(new ParameterizedThreadStart(function))
                            {
                                Name = $"dataLoader{i}"
                            };
                            dataLoader.Start((ulong)i);
                            return dataLoader;
                        }).ToArray();

                        // The main training loop.  Keep making mini-batches and giving them to the trainer.
                        // We will run until the learning rate has dropped by a factor of 1e-4.
                        while (trainer.GetLearningRate() >= 1e-4)
                        {
                            samples.DisposeElement();
                            labels.DisposeElement();
                            samples.Clear();
                            labels.Clear();

                            // make a 30-image mini-batch
                            while (samples.Count < 30)
                            {
                                data.Dequeue(out var temp);

                                samples.Add(temp.InputImage);
                                labels.Add(temp.LabelImage);

                                temp.Dispose();
                            }

                            LossMulticlassLogPerPixel.TrainOneStep(trainer, samples, labels);
                        }

                        // Training done, tell threads to stop and make sure to wait for them to finish before
                        // moving on.
                        data.Disable();
                        foreach (var thread in threads)
                            thread.Join();

                        // also wait for threaded processing to stop in the trainer.
                        trainer.GetNet();

                        net.Clean();
                        Console.WriteLine("saving network");
                        LossMulticlassLogPerPixel.Serialize(net, "semantic_segmentation_voc2012net.dnn");
                    }

                    // Make a copy of the network to use it for inference.
                    using (var anet = net.CloneAs(0))
                    {
                        Console.WriteLine("Testing the network...");

                        // Find the accuracy of the newly trained network on both the training and the validation sets.
                        Console.WriteLine($"train accuracy  :  {CalculateAccuracy(anet, GetPascalVoc2012TrainListing(args[0]))}");
                        Console.WriteLine($"val accuracy    :  {CalculateAccuracy(anet, GetPascalVoc2012ValListing(args[0]))}");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return 1;
            }

            return 0;
        }

        #region Helpers

        // Calculate the per-pixel accuracy on a dataset whose file names are supplied as a parameter.
        private static double CalculateAccuracy(LossMulticlassLogPerPixel anet, IEnumerable<ImageInfo> dataset)
        {
            var numRight = 0;
            var numWrong = 0;

            foreach (var imageInfo in dataset)
            {
                // Load the input image.
                using (var inputImage = Dlib.LoadImageAsMatrix<RgbPixel>(imageInfo.ImageFilename))
                {
                    // Load the ground-truth (RGB) labels.;
                    using (var rgbLabelImage = Dlib.LoadImageAsMatrix<RgbPixel>(imageInfo.LabelFilename))
                    {
                        // Create predictions for each pixel. At this point, the type of each prediction
                        // is an index (a value between 0 and 20). Note that the net may return an image
                        // that is not exactly the same size as the input.
                        using (var output = anet.Operator(inputImage))
                        using (var temp = output.First())
                        {
                            // Convert the indexes to RGB values.
                            using (var indexLabelImage = new Matrix<ushort>())
                            {
                                RgbLabelImageToIndexLabelImage(rgbLabelImage, indexLabelImage);

                                // Crop the net output to be exactly the same size as the input.
                                using (var chipDims = new ChipDims((uint)inputImage.Rows, (uint)inputImage.Columns))
                                using (var chipDetails = new ChipDetails(Dlib.CenteredRect(temp.Columns / 2, temp.Rows / 2,
                                                                         (uint)inputImage.Columns,
                                                                         (uint)inputImage.Rows),
                                                                         chipDims))
                                {
                                    using (var netOutput = Dlib.ExtractImageChip<ushort>(temp, chipDetails, InterpolationTypes.NearestNeighbor))
                                    {
                                        var nr = indexLabelImage.Rows;
                                        var nc = indexLabelImage.Columns;

                                        // Compare the predicted values to the ground-truth values.
                                        for (var r = 0; r < nr; ++r)
                                            for (var c = 0; c < nc; ++c)
                                            {
                                                var truth = indexLabelImage[r, c];
                                                if (truth != LossMulticlassLogPerPixel.LabelToIgnore)
                                                {
                                                    var prediction = netOutput[r, c];
                                                    if (prediction == truth)
                                                        ++numRight;
                                                    else
                                                        ++numWrong;
                                                }
                                            }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Return the accuracy estimate.
            return numRight / (double)(numRight + numWrong);
        }

        // The PASCAL VOC2012 dataset contains 20 ground-truth classes + background.  Each class
        // is represented using an RGB color value.  We associate each class also to an index in the
        // range [0, 20], used internally by the network.  To convert the ground-truth data to
        // something that the network can efficiently digest, we need to be able to map the RGB
        // values to the corresponding indexes.
        // Given an RGB representation, find the corresponding PASCAL VOC2012 class
        // (e.g., 'dog').
        private static Voc2012Class FindVoc2012Class(RgbPixel rgbLabel)
        {
            return Common.FindVoc2012Class(@class => rgbLabel == @class.RgbLabel);
        }

        // Read the list of image files belong to the "train" set of the PASCAL VOC2012 data.
        private static IEnumerable<ImageInfo> GetPascalVoc2012TrainListing(string voc2012Folder)
        {
            return GetPascalVoc2012Listing(voc2012Folder, "train");
        }

        // Read the list of image files belong to the "val" set of the PASCAL VOC2012 data.
        private static IEnumerable<ImageInfo> GetPascalVoc2012ValListing(string voc2012Folder)
        {
            return GetPascalVoc2012Listing(voc2012Folder, "val");
        }

        // Read the list of image files belonging to either the "train", "trainval", or "val" set
        // of the PASCAL VOC2012 data.
        private static IEnumerable<ImageInfo> GetPascalVoc2012Listing(string voc2012Folder,
                                                                      string file = "train" // "train", "trainval", or "val"
        )
        {
            var tst = Path.Combine(voc2012Folder, "ImageSets", "Segmentation", $"{file}.txt");
            var results = new List<ImageInfo>();

            using (var fs = new FileStream(tst, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sr = new StreamReader(fs))
            {
                while (!sr.EndOfStream)
                {
                    var basename = sr.ReadLine();
                    if (string.IsNullOrEmpty(basename))
                        continue;

                    var imageInfo = new ImageInfo
                    {
                        ImageFilename = Path.Combine(voc2012Folder, "JPEGImages", $"{basename}.jpg"),
                        LabelFilename = Path.Combine(voc2012Folder, "SegmentationClass", $"{basename}.png")
                    };

                    results.Add(imageInfo);
                }
            }

            return results;
        }

        private static Rectangle MakeRandomCroppingRectResNet(Matrix<RgbPixel> img, Rand rnd)
        {
            // figure out what rectangle we want to crop from the image
            const double mins = 0.466666666;
            const double maxs = 0.875;

            var scale = mins + rnd.GetRandomDouble() * (maxs - mins);
            var size = (uint)(scale * Math.Min(img.Rows, img.Columns));
            var rect = new Rectangle(size, size);
            // randomly shift the box around
            var offset = new Point((int)(rnd.GetRandom32BitNumber() % (img.Columns - rect.Width)),
                                   (int)(rnd.GetRandom32BitNumber() % (img.Rows - rect.Height)));

            return Dlib.MoveRect(rect, offset);
        }

        private static void RandomlyCropImage(Matrix<RgbPixel> inputImage, Matrix<ushort> labelImage, TrainingSample crop, Rand rnd)
        {
            var rect = MakeRandomCroppingRectResNet(inputImage, rnd);
            using (var chipDims = new ChipDims(227, 227))
            using (var chipDetails = new ChipDetails(rect, chipDims))
            {
                // Crop the input image.
                crop.InputImage = Dlib.ExtractImageChip<RgbPixel>(inputImage, chipDetails, InterpolationTypes.Bilinear);

                // Crop the labels correspondingly. However, note that here bilinear
                // interpolation would make absolutely no sense - you wouldn't say that
                // a bicycle is half-way between an aeroplane and a bird, would you?
                crop.LabelImage = Dlib.ExtractImageChip<ushort>(labelImage, chipDetails, InterpolationTypes.NearestNeighbor);

                // Also randomly flip the input image and the labels.
                if (rnd.GetRandomDouble() > 0.5)
                {
                    var tmpInput = Dlib.FlipLR(crop.InputImage);
                    var tmpLabel = Dlib.FlipLR(crop.LabelImage);
                    crop.InputImage?.Dispose();
                    crop.LabelImage?.Dispose();
                    crop.InputImage = tmpInput;
                    crop.LabelImage = tmpLabel;
                }

                // And then randomly adjust the colors.
                Dlib.ApplyRandomColorOffset(crop.InputImage, rnd);
            }
        }

        // Convert an RGB class label to an index in the range [0, 20].
        private static ushort RgbLabelToIndexLabel(RgbPixel rgbLabel)
        {
            return FindVoc2012Class(rgbLabel).Index;
        }

        // Convert an image containing RGB class labels to a corresponding
        // image containing indexes in the range [0, 20].
        private static void RgbLabelImageToIndexLabelImage(Matrix<RgbPixel> rgbLabelImage, Matrix<ushort> indexLabelImage)
        {
            var nr = rgbLabelImage.Rows;
            var nc = rgbLabelImage.Columns;

            indexLabelImage.SetSize(nr, nc);

            for (var r = 0; r < nr; ++r)
                for (var c = 0; c < nc; ++c)
                    indexLabelImage[r, c] = RgbLabelToIndexLabel(rgbLabelImage[r, c]);
        }

        #endregion

        #endregion

        // The names of the input image and the associated RGB label image in the PASCAL VOC 2012
        // data set.
        public sealed class ImageInfo
        {

            public string ImageFilename
            {
                get;
                set;
            }

            public string LabelFilename
            {
                get;
                set;
            }
        }

        // A single training sample. A mini-batch comprises many of these.
        private sealed class TrainingSample : IDisposable
        {

            #region Constructors

            public TrainingSample()
            {
                this.NativePtr = Marshal.AllocCoTaskMem(IntPtr.Size * 2);
                Marshal.WriteIntPtr(this.NativePtr, IntPtr.Size * 0, IntPtr.Zero);
                Marshal.WriteIntPtr(this.NativePtr, IntPtr.Size * 1, IntPtr.Zero);
            }

            public TrainingSample(IntPtr ptr)
            {
                this.NativePtr = ptr;
            }

            #endregion

            #region Properties

            public IntPtr NativePtr
            {
                get;
            }

            public Matrix<RgbPixel> InputImage
            {
                get => this.Read<Matrix<RgbPixel>>(0);
                set => this.Write(value, 0);
            }

            public Matrix<ushort> LabelImage // The ground-truth label of each pixel.
            {
                get => this.Read<Matrix<ushort>>(1);
                set => this.Write(value, 1);
            }

            #endregion

            #region Methods

            #region Helpers

            private T Read<T>(int offset)
            {
                var ret = Marshal.ReadIntPtr(this.NativePtr, IntPtr.Size * offset);
                if (ret == IntPtr.Zero)
                    return default(T);
                var bridge = ContainerBridgeRepository.Get<T>();
                return bridge.Create(ret);
            }

            private void Write<T>(T item, int offset)
                where T : DlibObject
            {
                var ptr = item?.NativePtr ?? IntPtr.Zero;
                Marshal.WriteIntPtr(this.NativePtr, IntPtr.Size * offset, ptr);
            }

            #endregion

            #endregion

            #region IDisposable Members

            private bool _IsDisposed;

            /// <summary>
            /// Releases all resources used by this <see cref="EnumerableDisposer{T}"/>.
            /// </summary>
            public void Dispose()
            {
                this.Dispose(true);
                //GC.SuppressFinalize(this);
            }

            /// <summary>
            /// Releases all resources used by this <see cref="EnumerableDisposer{T}"/>.
            /// </summary>
            /// <param name="disposing">Indicate value whether <see cref="IDisposable.Dispose"/> method was called.</param>
            private void Dispose(bool disposing)
            {
                if (this._IsDisposed)
                {
                    return;
                }

                this._IsDisposed = true;

                if (disposing)
                {
                    Marshal.FreeCoTaskMem(this.NativePtr);
                }
            }

            #endregion

        }
        
        private sealed class TrainingSampleContainerBridge : ContainerBridge<TrainingSample>
        {

            public override TrainingSample Create(IntPtr ptr, IParameter parameter = null)
            {
                return new TrainingSample(ptr);
            }

            public override IntPtr GetPtr(TrainingSample item)
            {
                return item.NativePtr;
            }

        }

    }

}