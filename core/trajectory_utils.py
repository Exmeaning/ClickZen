"""
轨迹处理工具类
包含Douglas-Peucker算法实现，用于简化滑动轨迹
"""

import math


def douglas_peucker(points, epsilon):
    """
    Douglas-Peucker算法实现
    用于简化轨迹点，保留关键转折点
    
    Args:
        points: [(x, y, time_ms), ...] 轨迹点列表
        epsilon: 简化阈值，值越大简化程度越高
    
    Returns:
        简化后的轨迹点列表
    """
    if len(points) <= 2:
        return points
    
    # 找到距离线段最远的点
    dmax = 0
    index = 0
    
    for i in range(1, len(points) - 1):
        d = perpendicular_distance(points[i], points[0], points[-1])
        if d > dmax:
            index = i
            dmax = d
    
    # 如果最大距离大于阈值，递归简化
    if dmax > epsilon:
        # 递归简化左右两部分
        left = douglas_peucker(points[:index + 1], epsilon)
        right = douglas_peucker(points[index:], epsilon)
        
        # 合并结果（去除重复的中间点）
        return left[:-1] + right
    else:
        # 只保留起点和终点
        return [points[0], points[-1]]


def perpendicular_distance(point, line_start, line_end):
    """
    计算点到线段的垂直距离
    
    Args:
        point: (x, y, time_ms) 待计算的点
        line_start: (x, y, time_ms) 线段起点
        line_end: (x, y, time_ms) 线段终点
    
    Returns:
        点到线段的垂直距离
    """
    x0, y0 = point[0], point[1]
    x1, y1 = line_start[0], line_start[1]
    x2, y2 = line_end[0], line_end[1]
    
    # 如果线段起点和终点相同
    if x1 == x2 and y1 == y2:
        return math.sqrt((x0 - x1) ** 2 + (y0 - y1) ** 2)
    
    # 计算点到线段的距离
    numerator = abs((y2 - y1) * x0 - (x2 - x1) * y0 + x2 * y1 - y2 * x1)
    denominator = math.sqrt((y2 - y1) ** 2 + (x2 - x1) ** 2)
    
    if denominator == 0:
        return 0
    
    return numerator / denominator


def simplify_trajectory(trajectory, epsilon=None):
    """
    简化轨迹，自动选择合适的阈值
    
    Args:
        trajectory: [(x, y, time_ms), ...] 原始轨迹
        epsilon: 简化阈值，None则自动计算
    
    Returns:
        简化后的轨迹
    """
    if len(trajectory) <= 2:
        return trajectory
    
    # 自动计算阈值
    if epsilon is None:
        # 计算轨迹的总长度
        total_length = 0
        for i in range(1, len(trajectory)):
            dx = trajectory[i][0] - trajectory[i-1][0]
            dy = trajectory[i][1] - trajectory[i-1][1]
            total_length += math.sqrt(dx ** 2 + dy ** 2)
        
        # 基于总长度的2-5%作为阈值，最小10像素，最大100像素
        # 增大阈值以减少轨迹点数量
        epsilon = max(10, min(100, total_length * 0.03))
    
    # 应用Douglas-Peucker算法
    simplified = douglas_peucker(trajectory, epsilon)
    
    # 限制最大点数为8个（避免过于复杂）
    if len(simplified) > 8:
        # 均匀采样
        step = len(simplified) // 6
        result = [simplified[0]]  # 起点
        for i in range(1, 6):
            result.append(simplified[i * step])
        result.append(simplified[-1])  # 终点
        return result
    
    # 确保至少保留3个点（如果原始轨迹有3个以上的点）
    if len(trajectory) >= 3 and len(simplified) < 3:
        # 添加中间点
        mid_index = len(trajectory) // 2
        simplified = [trajectory[0], trajectory[mid_index], trajectory[-1]]
    
    return simplified


def interpolate_trajectory(trajectory, target_duration_ms):
    """
    插值轨迹点，用于播放时生成平滑的滑动
    
    Args:
        trajectory: [(x, y, time_ms), ...] 简化的轨迹
        target_duration_ms: 目标持续时间（毫秒）
    
    Returns:
        插值后的轨迹点列表
    """
    if len(trajectory) <= 1:
        return trajectory
    
    # 如果轨迹点太少，直接返回
    if len(trajectory) <= 3:
        # 调整时间戳
        result = []
        original_duration = trajectory[-1][2] - trajectory[0][2]
        if original_duration <= 0:
            return trajectory
            
        time_scale = target_duration_ms / original_duration
        for x, y, t in trajectory:
            new_time = int((t - trajectory[0][2]) * time_scale)
            result.append((x, y, new_time))
        return result
    
    interpolated = []
    
    # 计算原始时间跨度
    original_duration = trajectory[-1][2] - trajectory[0][2]
    if original_duration <= 0:
        return trajectory
    
    # 时间缩放因子
    time_scale = target_duration_ms / original_duration if original_duration > 0 else 1.0
    
    # 插值间隔（毫秒） - 减少插值点数量
    interval_ms = max(50, target_duration_ms // 20)  # 最多20个点
    
    current_time = 0
    trajectory_index = 1
    
    # 添加第一个点
    interpolated.append((trajectory[0][0], trajectory[0][1], 0))
    
    while current_time <= target_duration_ms and trajectory_index < len(trajectory):
        prev_point = trajectory[trajectory_index - 1]
        next_point = trajectory[trajectory_index]
        
        # 计算当前段的时间范围（缩放后）
        segment_start_time = (prev_point[2] - trajectory[0][2]) * time_scale
        segment_end_time = (next_point[2] - trajectory[0][2]) * time_scale
        
        # 在当前段内插值
        while current_time <= segment_end_time:
            if segment_end_time > segment_start_time:
                # 计算插值比例
                t = (current_time - segment_start_time) / (segment_end_time - segment_start_time)
                t = max(0, min(1, t))
                
                # 线性插值
                x = int(prev_point[0] + (next_point[0] - prev_point[0]) * t)
                y = int(prev_point[1] + (next_point[1] - prev_point[1]) * t)
                
                # 避免重复点
                if not interpolated or (x, y) != (interpolated[-1][0], interpolated[-1][1]):
                    interpolated.append((x, y, int(current_time)))
            
            current_time += interval_ms
        
        trajectory_index += 1
    
    # 确保包含最后一个点
    if len(interpolated) == 0 or interpolated[-1][:2] != trajectory[-1][:2]:
        interpolated.append((trajectory[-1][0], trajectory[-1][1], target_duration_ms))
    
    # 限制最大点数
    if len(interpolated) > 10:
        # 均匀采样到10个点
        step = len(interpolated) // 10
        sampled = [interpolated[i * step] for i in range(10)]
        # 确保包含最后一个点
        if sampled[-1] != interpolated[-1]:
            sampled.append(interpolated[-1])
        return sampled
    
    return interpolated