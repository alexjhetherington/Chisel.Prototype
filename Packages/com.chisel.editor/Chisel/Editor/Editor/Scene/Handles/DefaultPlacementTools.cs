﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Chisel.Core;
using Chisel.Components;
using UnitySceneExtensions;
using UnityEditor.ShortcutManagement;

namespace Chisel.Editors
{
    public enum GeneratorModeState
    {
        None,

        Commit,
        Cancel,
        
        Update
    }

    public class ChiselShapePlacementToolInstance<PlacementToolDefinitionType, DefinitionType, Generator> : ChiselPlacementToolInstanceWithDefinition<PlacementToolDefinitionType, DefinitionType, Generator>
        // PlacementToolDefinition needs to be a ScriptableObject so we can create an Editor for it
        where PlacementToolDefinitionType : ChiselShapePlacementTool<DefinitionType>
        // We need the DefinitionType to be able to strongly type the Generator
        where DefinitionType    : IChiselGenerator, new()
        where Generator         : ChiselDefinedGeneratorComponent<DefinitionType>
    {
        public ChiselShapePlacementToolInstance(string toolName, string group)
        {
            internalToolName = toolName;
            internalGroup = group;
        }

        readonly string internalToolName;
        readonly string internalGroup;

        public override string ToolName => internalToolName;
        public override string Group    => internalGroup;

        protected IGeneratorHandleRenderer renderer = new GeneratorHandleRenderer();

        public override void OnSceneGUI(SceneView sceneView, Rect dragArea)
        {
            // TODO: handle snapping against own points
            // TODO: handle ability to 'commit' last point
            switch (ShapeExtrusionHandle.Do(dragArea, out Curve2D shape, out float height, out ChiselModel modelBeneathCursor, out Matrix4x4 transformation, Axis.Y))
            {
                case ShapeExtrusionState.Modified:
                case ShapeExtrusionState.Create:
                {
                    if (!generatedComponent)
                    {
                        if (height != 0)
                        {
                            var center2D = shape.Center;
                            var center3D = new Vector3(center2D.x, 0, center2D.y);
                            Transform parentTransform = null;
                            var model = ChiselModelManager.GetActiveModelOrCreate(modelBeneathCursor);
                            if (model != null) parentTransform = model.transform;
                            generatedComponent = ChiselComponentFactory.Create(generatorType, ToolName, parentTransform,
                                                                                  transformation * Matrix4x4.TRS(center3D, Quaternion.identity, Vector3.one))
                                                as ChiselDefinedGeneratorComponent<DefinitionType>;
                            shape.Center = Vector2.zero;
                            generatedComponent.definition.Reset();
                            generatedComponent.Operation = forceOperation ?? CSGOperationType.Additive;
                            PlacementToolDefinition.OnCreate(ref generatedComponent.definition, shape);
                            PlacementToolDefinition.OnUpdate(ref generatedComponent.definition, height);
                            generatedComponent.UpdateGenerator();
                        }
                    } else
                    {
                        generatedComponent.Operation = forceOperation ??
                                                  ((height < 0 && modelBeneathCursor) ?
                                                    CSGOperationType.Subtractive :
                                                    CSGOperationType.Additive);
                        PlacementToolDefinition.OnUpdate(ref generatedComponent.definition, height);
                        generatedComponent.OnValidate();
                    }
                    break;
                }
                
                case ShapeExtrusionState.Commit:        { Commit(generatedComponent.gameObject); break; }
                case ShapeExtrusionState.Cancel:        { Cancel(); break; }
            }

            if (ChiselOutlineRenderer.VisualizationMode != VisualizationMode.SimpleOutline)
                ChiselOutlineRenderer.VisualizationMode = VisualizationMode.SimpleOutline;

            renderer.matrix = transformation;
            PlacementToolDefinition.OnPaint(renderer, shape, height);
        }
    }
    
    public class ChiselBoundsPlacementToolInstance<PlacementToolDefinitionType, DefinitionType, Generator> 
        : ChiselPlacementToolInstanceWithDefinition<PlacementToolDefinitionType, DefinitionType, Generator>
        // PlacementToolDefinition needs to be a ScriptableObject so we can create an Editor for it
        where PlacementToolDefinitionType : ChiselBoundsPlacementTool<DefinitionType>
        // We need the DefinitionType to be able to strongly type the Generator
        where DefinitionType    : IChiselGenerator, new()
        where Generator         : ChiselDefinedGeneratorComponent<DefinitionType>
    {
        public ChiselBoundsPlacementToolInstance(string toolName, string group)
        {
            internalToolName = toolName;
            internalGroup = group;
        }

        readonly string internalToolName;
        readonly string internalGroup;

        public override string ToolName => internalToolName;
        public override string Group => internalGroup;

        Vector3 componentPosition   = Vector3.zero;
        Vector3 upAxis              = Vector3.zero;

        protected IGeneratorHandleRenderer renderer = new GeneratorHandleRenderer();

        const float kMinimumAxisLength = 0.0001f;

        public override void OnSceneGUI(SceneView sceneView, Rect dragArea)
        {
            var generatoreModeFlags = PlacementToolDefinition.PlacementFlags;

            if ((generatoreModeFlags & (PlacementFlags.HeightEqualsHalfXZ | PlacementFlags.HeightEqualsXZ)) != 0)
                generatoreModeFlags |= PlacementFlags.SameLengthXZ;

            if (Event.current.shift)
                generatoreModeFlags |= PlacementFlags.UseLastHeight;

            switch (RectangleExtrusionHandle.Do(dragArea, out Bounds bounds, out float height, out ChiselModel modelBeneathCursor, out Matrix4x4 transformation, generatoreModeFlags, Axis.Y))
            {
                case GeneratorModeState.Update:
                {
                    if (!generatedComponent)
                    {
                        var size = bounds.size;
                        if (Mathf.Abs(size.x) >= kMinimumAxisLength &&
                            Mathf.Abs(size.y) >= kMinimumAxisLength &&
                            Mathf.Abs(size.z) >= kMinimumAxisLength)
                        {
                            // Create the generator GameObject
                            Transform parentTransform = null;
                            var model = ChiselModelManager.GetActiveModelOrCreate(modelBeneathCursor);
                            if (model != null) parentTransform = model.transform;
                            generatedComponent  = ChiselComponentFactory.Create(generatorType, ToolName, parentTransform, transformation) 
                                                as ChiselDefinedGeneratorComponent<DefinitionType>;
                            componentPosition   = generatedComponent.transform.localPosition;
                            upAxis              = generatedComponent.transform.up;

                            generatedComponent.definition.Reset();
                            generatedComponent.Operation = forceOperation ?? CSGOperationType.Additive;
                            PlacementToolDefinition.OnCreate(ref generatedComponent.definition);
                            PlacementToolDefinition.OnUpdate(ref generatedComponent.definition, bounds);
                            generatedComponent.OnValidate();

                            if ((generatoreModeFlags & PlacementFlags.GenerateFromCenterY) == PlacementFlags.GenerateFromCenterY)
                                generatedComponent.transform.localPosition = componentPosition - ((upAxis * height) * 0.5f);
                            generatedComponent.UpdateGenerator();
                        }
                    } else
                    {
                        var size = bounds.size;
                        if (Mathf.Abs(size.x) < kMinimumAxisLength &&
                            Mathf.Abs(size.y) < kMinimumAxisLength &&
                            Mathf.Abs(size.z) < kMinimumAxisLength)
                        {
                            UnityEngine.Object.DestroyImmediate(generatedComponent);
                            return;
                        }

                        // Update the generator GameObject
                        ChiselComponentFactory.SetTransform(generatedComponent, transformation);
                        if ((generatoreModeFlags & PlacementFlags.AlwaysFaceUp) == PlacementFlags.AlwaysFaceCameraXZ)
                            generatedComponent.Operation = forceOperation ?? CSGOperationType.Additive;
                        else
                            generatedComponent.Operation = forceOperation ??
                                                    ((height < 0 && modelBeneathCursor) ?
                                                    CSGOperationType.Subtractive :
                                                    CSGOperationType.Additive);
                        PlacementToolDefinition.OnUpdate(ref generatedComponent.definition, bounds);
                        generatedComponent.OnValidate();
                        if ((generatoreModeFlags & PlacementFlags.GenerateFromCenterY) == PlacementFlags.GenerateFromCenterY)
                            generatedComponent.transform.localPosition = componentPosition - ((upAxis * height) * 0.5f);
                    }
                    break;
                }
                
                case GeneratorModeState.Commit:     { if (generatedComponent) Commit(generatedComponent.gameObject); else Cancel(); break; }
                case GeneratorModeState.Cancel:     { Cancel(); break; }
            }

            if (ChiselOutlineRenderer.VisualizationMode != VisualizationMode.SimpleOutline)
                ChiselOutlineRenderer.VisualizationMode = VisualizationMode.SimpleOutline;
            renderer.matrix = transformation;
            PlacementToolDefinition.OnPaint(renderer, bounds);
        }
    }
}