﻿// Copyright 2018 Kevin Kovalchik & Christopher Hughes
// 
// Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
// 
//        http://www.apache.org/licenses/LICENSE-2.0
// 
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
// Kevin Kovalchik and Christopher Hughes do not claim copyright of
// any third-party libraries ditributed with RawTools. All third party
// licenses are provided in accompanying files as outline in the NOTICE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ThermoFisher;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.Data;
using ThermoFisher.CommonCore.Data.FilterEnums;
using System.Collections;
using RawTools.Data.Containers;
using RawTools.Data.Collections;
using RawTools.Data.Extraction;
using RawTools.Utilities;
using RawTools.Algorithms;
using RawTools.WorkFlows;

namespace RawTools.Algorithms.Analyze
{
    static class MetaDataProcessing
    {
        public static ScanMetaDataCollectionDDA AggregateMetaDataDDA(CentroidStreamCollection centroidStreams, SegmentScanCollection segmentScans, MethodDataContainer methodData,
            PrecursorScanCollection precursorScans, TrailerExtraCollection trailerExtras, PrecursorMassCollection precursorMasses,
            RetentionTimeCollection retentionTimes, ScanDependentsCollections scanDependents, ScanIndex index)
        {
            ProgressIndicator progress = new ProgressIndicator(index.ScanEnumerators[MSOrderType.Any].Count(),
                "Formatting scan meta data");

            ScanMetaDataCollectionDDA metaData = new ScanMetaDataCollectionDDA();
            
            double isoWindow = ParseMetaData.Ms1IsoWindow(methodData);

            metaData.MS2ScansPerCycle = ParseMetaData.MS2ScansPerCycle(scanDependents, index);

            metaData.FillTime = ParseMetaData.FillTimes(trailerExtras, index);

            metaData.DutyCycle = ParseMetaData.DutyCycle(retentionTimes, index);

            metaData.IntensityDistribution = ParseMetaData.IntensityDistributions(centroidStreams, segmentScans, index);

            metaData.SummedIntensity = ParseMetaData.SummedIntensities(centroidStreams, segmentScans, index);

            metaData.FractionConsumingTop80PercentTotalIntensity = ParseMetaData.Top80Frac(centroidStreams, segmentScans, index);

            metaData.Ms1IsolationInterference = ParseMetaData.Ms1Interference(centroidStreams, precursorMasses, trailerExtras,
                precursorScans, index, isoWindow);

            return metaData;
        }

        public static MetricsData GetMetricsDataDDA(ScanMetaDataCollectionDDA metaData, IRawDataPlus rawFile, MethodDataContainer methodData,
            WorkflowParameters parameters, RetentionTimeCollection retentionTimes, ScanIndex index, PrecursorPeakCollection peakData,
            QuantDataCollection quantData = null)
        {
            MetricsData metricsData = new MetricsData();

            metricsData.RawFileName = parameters.RawFileName;
            metricsData.Instrument = methodData.Instrument;
            metricsData.MS1Analyzer = methodData.MassAnalyzers[MSOrderType.Ms];
            metricsData.MS2Analyzer = methodData.MassAnalyzers[MSOrderType.Ms2];

            metricsData.TotalAnalysisTime = retentionTimes[index.ScanEnumerators[MSOrderType.Any].Last()] -
                retentionTimes[index.ScanEnumerators[MSOrderType.Any].First()];

            metricsData.TotalScans = index.allScans.Count();
            metricsData.MS1Scans = index.ScanEnumerators[MSOrderType.Ms].Length;
            metricsData.MS2Scans = index.ScanEnumerators[MSOrderType.Ms2].Length;

            if (methodData.AnalysisOrder == MSOrderType.Ms3)
            {
                metricsData.MS3Analyzer = methodData.MassAnalyzers[MSOrderType.Ms3];
                metricsData.MS3Scans = index.ScanEnumerators[MSOrderType.Ms3].Length;
            }
            else
            {
                metricsData.MS3Analyzer = MassAnalyzerType.Any;
                metricsData.MS3Scans = 0;
            }

            metricsData.MSOrder = methodData.AnalysisOrder;

            metricsData.MedianSummedMS2Intensity = (from x in index.ScanEnumerators[MSOrderType.Ms2]
                                                    select metaData.SummedIntensity[x]).ToArray().Percentile(50);

            metricsData.MedianPrecursorIntensity = (from x in peakData.Keys select peakData[x].ParentIntensity).ToArray().Percentile(50);

            metricsData.MedianMS1FillTime = (from x in index.ScanEnumerators[MSOrderType.Ms]
                                             select metaData.FillTime[x]).ToArray().Percentile(50);

            metricsData.MedianMS2FillTime = (from x in index.ScanEnumerators[MSOrderType.Ms2]
                                             select metaData.FillTime[x]).ToArray().Percentile(50);

            metricsData.MedianMS3FillTime = (from x in index.ScanEnumerators[MSOrderType.Ms3]
                                             select metaData.FillTime[x]).ToArray().Percentile(50);

            metricsData.MeanTopN = metaData.MS2ScansPerCycle.MeanFromDict();
            
            metricsData.MeanDutyCycle = (from x in index.ScanEnumerators[MSOrderType.Ms3]
                                         select metaData.DutyCycle[x]).ToArray().Mean();
            
            metricsData.MedianMs2FractionConsumingTop80PercentTotalIntensity =
                (from x in index.ScanEnumerators[MSOrderType.Ms3]
                 select metaData.FractionConsumingTop80PercentTotalIntensity[x]).ToArray().Percentile(50);
            
            metricsData.MS1ScanRate = metricsData.MS1Scans / metricsData.TotalAnalysisTime;
            metricsData.MS2ScanRate = metricsData.MS2Scans / metricsData.TotalAnalysisTime;
            metricsData.MS3ScanRate = metricsData.MS3Scans / metricsData.TotalAnalysisTime;

            metricsData.MedianBaselinePeakWidth = peakData.PeakShapeMedians.Width.P10;
            metricsData.MedianHalfHeightPeakWidth = peakData.PeakShapeMedians.Width.P50;

            // we can't access the instrument method in Linux, so we will assume the gradient length is the length of the MS acquisition
            metricsData.Gradient = retentionTimes[index.allScans.Keys.Max()];
            metricsData.PeakCapacity = metricsData.Gradient / metricsData.MedianHalfHeightPeakWidth;

            metricsData.MedianAsymmetryFactor = peakData.PeakShapeMedians.Asymmetry.P10;

            // add isolation interference
            metricsData.MedianMs1IsolationInterference = (from scan in index.ScanEnumerators[methodData.AnalysisOrder]
                                                          select metaData.Ms1IsolationInterference[scan]).ToArray().Percentile(50);

            // now add the quant meta data, if quant was performed
            double medianReporterIntensity = 0;
            QuantMetaData quantMetaData = new QuantMetaData();
            SerializableDictionary<string, double> medianReporterIntensityByChannel = new SerializableDictionary<string, double>();
            if (quantData != null)
            {
                string reagent = quantData.LabelingReagents;
                string[] allTags = new LabelingReagents().Reagents[reagent].Labels;
                List<double> allChannels = new List<double>();
                Dictionary<string, List<double>> byChannel = new Dictionary<string, List<double>>();
                foreach (string tag in allTags)
                {
                    byChannel.Add(tag, new List<double>());
                }
                foreach (int scan in index.ScanEnumerators[methodData.AnalysisOrder])
                {
                    foreach (string tag in allTags)
                    {
                        byChannel[tag].Add(quantData[scan][tag].Intensity);
                        allChannels.Add(quantData[scan][tag].Intensity);
                    }

                }
                medianReporterIntensity = allChannels.ToArray().Percentile(50);

                foreach (string tag in allTags)
                {
                    medianReporterIntensityByChannel[tag] = byChannel[tag].ToArray().Percentile(50);
                }

                quantMetaData.medianReporterIntensity = medianReporterIntensity;
                quantMetaData.medianReporterIntensityByChannel = medianReporterIntensityByChannel;
                quantMetaData.quantTags = allTags;
                metricsData.QuantMeta = quantMetaData;
                metricsData.IncludesQuant = true;
            }
            return metricsData;
        }

        /* The instrument method is inaccesible on Linux and Mac, so we won't use this method to get the gradient time. Instead we will
         * approximate that it is the retention time of the last scan, which should usually be a safe assumption. */
        public static double GradientTime(string LcMethod)
        {
            Regex rx = new Regex(@"(?:Mixture\s+\[%\S+\]\s+)([\S\s]+)(?:\n\nPre)");
            Match gradientMatch = rx.Match(LcMethod);
            string gradient = gradientMatch.Groups[1].Value;
            string[] lines = gradient.ToString().Split('\n');
            List<string[]> gradientStrings = new List<string[]>();

            foreach (var line in lines)
            {
                gradientStrings.Add(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }

            double gradientStart = 0;
            double gradientEnd = 0;

            for (int i = 0; i < gradientStrings.Count() - 1; i++)
            {
                if (gradientStrings.ElementAt(i + 1)[3] != gradientStrings.ElementAt(i)[3])
                {
                    gradientStart = Convert.ToDouble(gradientStrings.ElementAt(i)[0].Replace(':', '.'));
                    break;
                }
            }
            for (int i = gradientStrings.Count() - 1; i >= 0; i--)
            {
                if (gradientStrings.ElementAt(i - 1)[3] == gradientStrings.ElementAt(i)[3])
                {
                    gradientEnd = Convert.ToDouble(gradientStrings.ElementAt(i - 1)[0].Replace(':', '.'));
                    break;
                }
            }
            return gradientEnd - gradientStart;
        }
    }
}