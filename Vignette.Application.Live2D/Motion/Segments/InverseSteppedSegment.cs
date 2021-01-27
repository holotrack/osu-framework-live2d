﻿// Copyright 2020 - 2021 Vignette Project
// Licensed under MIT. See LICENSE for details.

namespace Vignette.Application.Live2D.Motion.Segments
{
    public class InverseSteppedSegment : Segment
    {
        public InverseSteppedSegment()
            : base(2)
        {
        }

        public override double ValueAt(double time)
        {
            return Points[1].Value;
        }
    }
}
