﻿// Copyright 2020 - 2021 Vignette Project
// Licensed under MIT. See LICENSE for details.

using System.Collections.Generic;
using System.Linq;
using Vignette.Application.Live2D.Id;
using Vignette.Application.Live2D.Model;
using Vignette.Application.Live2D.Motion.Segments;

namespace Vignette.Application.Live2D.Motion
{
    public class Curve
    {
        public IEnumerable<Segment> Segments { get; set; }

        public MotionTarget TargetType { get; set; }

        public CubismId Effect { get; set; }

        public CubismPart Part { get; set; }

        public CubismParameter Parameter { get; set; }

        public double FadeInTime { get; set; }

        public double FadeOutTime { get; set; }

        public double ValueAt(double time)
        {
            foreach (var segment in Segments)
            {
                var points = segment.Points;
                if (time <= points.Last().Time)
                    return (points[0].Time <= time) ? segment.ValueAt(time) : points[0].Value;
            }

            return Segments.Last().Points.Last().Value;
        }
    }
}
