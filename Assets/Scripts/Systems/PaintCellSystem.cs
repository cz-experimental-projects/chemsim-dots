﻿using Aspects;
using Components;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Utils;

namespace Systems
{
    [BurstCompile]
    public partial struct PaintCellSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
        }

        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.TryGetSingleton<GridComponent>(out var grid))
            {
                #region Instantiate Newly Spawned Cells Data

                var query = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<NewlyInstantiatedCellTag>());
                var newlyCreatedCells = query.ToEntityArray(Allocator.Temp);
                var newlyCreatedCellAspects = new NativeArray<CellAspect>(newlyCreatedCells.Length, Allocator.TempJob);

                for (var i = 0; i < newlyCreatedCells.Length; i++)
                {
                    newlyCreatedCellAspects[i] = SystemAPI.GetAspectRW<CellAspect>(newlyCreatedCells[i]);
                }

                new SetupNewlyInstantiatedCellsJob
                    {
                        Grid = grid,
                        CellAspects = newlyCreatedCellAspects
                    }
                    .Schedule(newlyCreatedCellAspects.Length, 16)
                    .Complete();

                var entityCommandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                    .CreateCommandBuffer(state.WorldUnmanaged);
                entityCommandBuffer.RemoveComponent<NewlyInstantiatedCellTag>(newlyCreatedCells);

                newlyCreatedCells.Dispose();
                newlyCreatedCellAspects.Dispose();

                #endregion

                #region Spawn New Cells

                var cellPrefab = SystemAPI.GetSingleton<CellPrefabComponent>();
                var brush = SystemAPI.GetSingleton<BrushComponent>();
                var cellPositions = SystemAPI.GetSingletonBuffer<CellPosition>();

                foreach (var spawnCellQueue in SystemAPI.Query<DynamicBuffer<CellSpawnQueue>>())
                {
                    if (spawnCellQueue.Length <= 0) continue;
                    var resultingPositions = new NativeList<uint2>(Allocator.TempJob);

                    new CalculatePositionsJob
                        {
                            Brush = brush,
                            Grid = grid,
                            Results = resultingPositions,
                            SpawnQueues = spawnCellQueue,
                            CellPositions = cellPositions
                        }
                        .Schedule(spawnCellQueue.Length, 64)
                        .Complete();

                    spawnCellQueue.Clear();

                    var count = resultingPositions.Length;
                    var entities = new NativeArray<Entity>(count, Allocator.Temp);

                    entityCommandBuffer.Instantiate(cellPrefab.CellPrefab, entities);
                    for (var i = 0; i < count; i++)
                    {
                        var position = resultingPositions[i];
                        var cellPosition = new CellPosition { Position = position };

                        var entity = entities[i];
                        cellPositions.Add(cellPosition);

                        entityCommandBuffer.SetComponent(entity, new CellComponent
                        {
                            Position = position
                        });
                        entityCommandBuffer.SetComponent(entity, new CellMaterialComponent
                        {
                            CellType = brush.CellType
                        });
                        entityCommandBuffer.AddComponent<NewlyInstantiatedCellTag>(entities);
                    }

                    resultingPositions.Dispose();
                    entities.Dispose();
                }

                #endregion
            }
        }
    }

    [BurstCompile]
    public struct CalculatePositionsJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeList<uint2> Results;

        [ReadOnly] public GridComponent Grid;
        [ReadOnly] public BrushComponent Brush;
        [ReadOnly] public DynamicBuffer<CellSpawnQueue> SpawnQueues;
        [ReadOnly] public DynamicBuffer<CellPosition> CellPositions;

        [BurstCompile]
        public void Execute(int index)
        {
            var gridPosition = Grid.WorldToGrid(SpawnQueues[index].Position);
            if (!Grid.ValidPosition(gridPosition)) return;

            var centerX = gridPosition.x;
            var centerY = gridPosition.y;
            var rr = Brush.BrushSize - 1;

            for (var r = 0; r < rr; r++)
            {
                for (var x = centerX - rr; x <= centerX + rr; x++)
                {
                    for (var y = centerY - rr; y <= centerY + rr; y++)
                    {
                        if (!(math.sqrt((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY)) <= r + 1) ||
                            !(math.sqrt((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY)) > r)) continue;

                        var value = new uint2(x, y);
                        if (Results.Contains(value) || CellPositions.Contains(new CellPosition { Position = value }))
                            continue;
                        Results.Add(value);
                    }
                }
            }

            var center = new uint2(centerX, centerY);
            if (Results.Contains(center) || CellPositions.Contains(new CellPosition { Position = center })) return;
            Results.Add(center);
        }
    }

    [BurstCompile]
    public struct SetupNewlyInstantiatedCellsJob : IJobParallelFor
    {
        [ReadOnly] public GridComponent Grid;
        [ReadOnly] public NativeArray<CellAspect> CellAspects;

        [BurstCompile]
        public void Execute(int index)
        {
            var cellAspect = CellAspects[index];
            cellAspect.Move(Grid, cellAspect.Cell.ValueRO.Position);
        }
    }
}