﻿using Bonsai.Expressions;
using OpenCV.Net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;

namespace Bonsai.ONIX
{
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Calculates uncalibrated 3D position of a single photodiode for V1 base stations.")]
    public class TS4231V1FrameToPosition : SingleArgumentExpressionBuilder
    {
        private class HubFinder : ExpressionVisitor
        {
            public oni.Hub Hub { get; private set; }

            public Expression GetUpstreamHub(Expression expression)
            {
                return Visit(expression);
            }

            protected override Expression VisitConstant(ConstantExpression node)
            {
                if (node.Type == typeof(TS4231V1Device))
                {
                    var device = node.Value as TS4231V1Device;
                    if (device.Hub != null)
                    {
                        // Because this TS4321V1FrameToPosition only accepts TS4321V1DataFrames,
                        // we have some assurance that we dont need to keep track of multiple
                        // upstream TS4321V1Devices
                        Hub = device.Hub;
                    }
                }

                return base.VisitConstant(node);
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                return base.VisitParameter(node);
            }

            protected override Expression VisitExtension(Expression node)
            {
                // TODO: This is a hack.
                // - I should not be using reflection to access properties of private MulticastBranchExpression
                // - There are probably ways to use the Expression helpers that come with Bonsai to do this, but
                //   I dont know how.
                // - I have no clue how brittle this is across different workflow topologies. For instance, I have confirmed
                //   that this currently does _not_ work across publish/subscribe subject.
                var type = node.GetType();
                if (type.Name == "MulticastBranchExpression")
                {
                    var source = type.GetRuntimeProperty("Source").GetValue(node);
                    Visit(source as Expression);
                }

                return base.VisitExtension(node);
            }
        }

        // Template pattern
        private static readonly bool[] Template = {
        //  bad    skip   axis   sweep
            false, false, false, false,
            false, true,  false, false,
            false, false, false, true, // axis 0, station 0
            false, false, true,  false,
            false, true,  true,  false,
            false, false, false, true, // axis 1, station 0
            false, true,  false, false,
            false, false, false, false,
            false, false, false, true, // axis 0, station 1
            false, true,  true,  false,
            false, false, true,  false,
            false, false, false, true  // axis 1, station 1
        };

        // Max seconds it should take to receive an entire valid template
        // TODO: Figure out the correct value...
        //const double max_packet_duration = 0.03;

        private readonly Queue<double> pulseTimes;
        private readonly Queue<double> pulseWidths;
        private readonly Queue<bool> pulseParse;
        private readonly Queue<ulong> pulseDataClock;
        private readonly Queue<ulong> pulseFrameClock;

        // Origins in Mat format
        private Mat p, q;

        // Calculated position
        private Mat position = new Mat(3, 1, Depth.F64, 1);
        private ulong positionDataClock;
        private ulong positionFrameClock;

        // Base station IR sweep frequency in Hz
        private const double SweepFrequency = 60;

        // Rate of clock timing the IR reception times
        private double? DataClockHz;

        public TS4231V1FrameToPosition()
        {
            P = new Point3d(0, 0, 0);
            Q = new Point3d(1, 0, 0);

            var fill0 = new double[Template.Length / 4];
            var fill1 = new bool[Template.Length];
            var fill2 = new ulong[Template.Length / 4];

            pulseTimes = new Queue<double>(fill0);
            pulseWidths = new Queue<double>(fill0);
            pulseParse = new Queue<bool>(fill1);
            pulseDataClock = new Queue<ulong>(fill2);
            pulseFrameClock = new Queue<ulong>(fill2);
        }

        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var source = arguments.First();

            var hubFinder = new HubFinder();
            hubFinder.GetUpstreamHub(source);

            if (hubFinder.Hub == null)
            {
                throw new Bonsai.WorkflowBuildException("Upstream TS4321V1Device does not have valid hardware link.");
            }

            DataClockHz = hubFinder.Hub.ClockHz;

            var thisType = GetType();
            var method = thisType.GetMethod(nameof(Process));
            var instance = Expression.Constant(this);
            return Expression.Call(instance, method, new[] { source });
        }

        private bool Decode(TS4231V1DataFrame source)
        {
            // Push pulse time into buffer and pop oldest
            pulseTimes.Dequeue();
            pulseTimes.Enqueue(source.HubSyncCounter / DataClockHz ?? double.NaN);

            pulseDataClock.Dequeue();
            pulseDataClock.Enqueue(source.HubSyncCounter);

            pulseFrameClock.Dequeue();
            pulseFrameClock.Enqueue(source.Clock);

            // Push pulse width into buffer and pop oldest
            pulseWidths.Dequeue();
            pulseWidths.Enqueue(source.PulseWidth / DataClockHz ?? double.NaN);

            // Push pulse parse info into buffer and pop oldest 4x
            pulseParse.Dequeue();
            pulseParse.Dequeue();
            pulseParse.Dequeue();
            pulseParse.Dequeue();

            var type = source.PulseType;
            pulseParse.Enqueue(type == -1); // bad
            pulseParse.Enqueue(type >= 4 & type != 8); // skip
            pulseParse.Enqueue(type % 2 == 1 & type != 8); // axis
            pulseParse.Enqueue(type == 8); // sweep

            // Test template match and make sure time between pulses does 
            // not integrate to more than two periods
            if (!pulseParse.SequenceEqual(Template) || pulseTimes.Last() - pulseTimes.First() > 2 / SweepFrequency)
            {
                return false;
            }

            // Time is the mean of the data used
            positionDataClock = pulseDataClock.ElementAt(Template.Length / 8);
            positionFrameClock = pulseFrameClock.ElementAt(Template.Length / 8);

            var time = pulseTimes.ToArray();
            var width = pulseWidths.ToArray();

            var t11 = time[2] + width[2] / 2 - time[0];
            var t21 = time[5] + width[5] / 2 - time[3];
            var theta0 = 2 * Math.PI * SweepFrequency * t11 - Math.PI / 2;
            var gamma0 = 2 * Math.PI * SweepFrequency * t21 - Math.PI / 2;

            var u = new Mat(3, 1, Depth.F64, 1);
            u[0] = new Scalar(Math.Tan(theta0));
            u[1] = new Scalar(Math.Tan(gamma0));
            u[2] = new Scalar(1);
            CV.Normalize(u, u);

            var t12 = time[8] + width[8] / 2 - time[7];
            var t22 = time[11] + width[11] / 2 - time[10];
            var theta1 = 2 * Math.PI * SweepFrequency * t12 - Math.PI / 2;
            var gamma1 = 2 * Math.PI * SweepFrequency * t22 - Math.PI / 2;

            var v = new Mat(3, 1, Depth.F64, 1);
            v[0] = new Scalar(Math.Tan(theta1));
            v[1] = new Scalar(Math.Tan(gamma1));
            v[2] = new Scalar(1);
            CV.Normalize(v, v);

            // Base station origin vector
            var d = q - p;

            // Linear transform
            // A = [a11 a12]
            //     [a21 a22]
            var a11 = 1.0;
            var a12 = -CV.DotProduct(u, v);
            var a21 = CV.DotProduct(u, v);
            var a22 = -1.0;

            // Result
            // B = [b1]
            //     [b2]
            var b1 = CV.DotProduct(u, d);
            var b2 = CV.DotProduct(v, d);

            // Solve Ax = B
            var x2 = (b2 - (b1 * a21) / a11) / (a22 - (a12 * a21) / a11);
            var x1 = (b1 - a12 * x2) / a11;

            // TODO: If non-singular solution else send NaNs
            //if (x)
            //{
            var p1 = p + x1 * u;
            var q1 = q + x2 * v;
            //}

            // Or single matrix with columns as results
            position = 0.5 * (p1 + q1);

            return true;
        }

        public IObservable<Position3D> Process(IObservable<TS4231V1DataFrame> source)
        {
            return source.Where(input => input.Index == Index)
                         .Where(input => Decode(input))
                         .Select(input => new Position3D(positionFrameClock, positionDataClock, position));
        }

        [Description("Index of the photodiode 3D position is calculated for.")]
        public int Index { get; set; }

        [Description("Position of the first base station in units of your choice.")]
        public Point3d P
        {
            get
            {
                return new Point3d(p[0].Val0, p[1].Val0, p[2].Val0);
            }
            set
            {
                p = new Mat(3, 1, Depth.F64, 1);
                p[0] = new Scalar(value.X);
                p[1] = new Scalar(value.Y);
                p[2] = new Scalar(value.Z);
            }
        }

        [Description("Position of the second base station in units of your choice.")]
        public Point3d Q
        {
            get
            {
                return new Point3d(q[0].Val0, q[1].Val0, q[2].Val0);
            }
            set
            {
                q = new Mat(3, 1, Depth.F64, 1);
                q[0] = new Scalar(value.X);
                q[1] = new Scalar(value.Y);
                q[2] = new Scalar(value.Z);
            }
        }
    }
}
