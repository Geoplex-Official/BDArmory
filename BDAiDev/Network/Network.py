import tensorflow as tf
import keras

import numpy as mp

from keras import layers


    #2v2 ai for KSP BDA
teamMate_isAlive = 0
teamMate_isDamaged = 0
teamMate_distance = 0
teamMate_relativeAngle = 0
teamMate_absoluteAngleX = 0
teamMate_absoluteAngleY = 0
teamMate_absoluteAngleZ = 0
teamMate_altitude = 0
teamMate_speed = 0
teamMate_relativeSpeed = 0
teamMate_acceleration = 0
teamMate_distFromEnemy1 = 0
teamMate_distFromEnemy2 = 0
    #Teamate Status
class TeamMateStatus():

    def setTeamMateStatus(isAlive, isDamaged, distance, relativeAngle, absoluteAngleX, absoluteAngleY, absoluteAngleZ, altitude, speed, relativeSpeed, acceleration, distFromEnemy1, distFromEnemy2):
        """Tells the ai the status of its teamate in real time

        # Example

        `setTeamMateStatus(True, False, 5700, 30, 50, 90, 270, 1500, 300, 50, 20, 7000, 8000)`

        if the team mate is dead you can just do this

        `setTeamMateStatus(False)`

        # Arguments

        Note: The parameters listed here are in numerical order in terms of their place on the input list

        `isAlive` is a boolean to tell the ai if its team mate is alive or not

        `isDamaged` is a boolean to tell the ai if its team mate has taken damage
        
        `distance` is an aproximation of the distance between the craft's team mate and itself, 
        make sure to have more exact values as they are smaller and more apporximated values for larger values, to keep realism

        `relativeAngle` is the relative angle to the craft's team mate

        `absoluteAngleX` is the global angle the team mate is pointing in terms of the x-axis

        `absoluteAngleY` is the global angle the team mate is pointing in terms of the y-axis

        `absoluteAngleZ` is the global angle the team mate is pointing in terms of the z-axis

        `altitude` is the approximate (use rounding) altitude of the team mate

        `speed` is the approximate (use rounding) airspeed of the team mate

        `relativeSpeed` is the approximate (use rounding) speed of the team mate

        `acceleration` is the approximate (use rounding) acceleration of the team mate, in meters per second squared because twr is dampened by drag

        `distFromEnemy1` is the approximate (use rounding) distance the team mate is from the first enemy craft, set this to `1j` if this enemy is dead

        `distFromEnemy1` is the approximate (use rounding) distance the team mate is from the second enemy craft, set this to `1j` if this enemy is dead
        """
        teamMate_isAlive, teamMate_isDamaged, teamMate_distance, teamMate_relativeAngle, teamMate_absoluteAngleX, teamMate_absoluteAngleY, teamMate_absoluteAngleZ, teamMate_altitude, teamMate_speed, teamMate_relativeSpeed, teamMate_acceleration, teamMate_distFromEnemy1, teamMate_distFromEnemy2 = isAlive, isDamaged, distance, relativeAngle, absoluteAngleX, absoluteAngleY, absoluteAngleZ, altitude, speed, relativeSpeed, acceleration, distFromEnemy1, distFromEnemy2
        if(teamMate_isAlive == False):
            teamMate_isAlive = 0
            teamMate_isDamaged = 1j
            teamMate_distance = 1j
            teamMate_relativeAngle = 1j
            teamMate_absoluteAngleX = 1j
            teamMate_absoluteAngleY = 1j
            teamMate_absoluteAngleZ = 1j
            teamMate_altitude = 1j
            teamMate_speed = 1j
            teamMate_relativeSpeed = 1j
            teamMate_acceleration = 1j
            teamMate_distFromEnemy1 = 1j
            teamMate_distFromEnemy2 = 1j
        else:
            teamMate_isAlive = 1
        if(teamMate_isDamaged == True):
            teamMate_isDamaged = 1
        else:
            teamMate_isDamaged = 0

    def returnTeamMateStatus():
        """Returns the team mate's status, an internal function that you don't need to use"""
        return teamMate_isAlive, teamMate_isDamaged, teamMate_distance, teamMate_relativeAngle, teamMate_absoluteAngleX, teamMate_absoluteAngleY, teamMate_absoluteAngleZ, teamMate_altitude, teamMate_speed, teamMate_relativeSpeed, teamMate_acceleration, teamMate_distFromEnemy1, teamMate_distFromEnemy2