# 2D spring mesh
This repo implements a 2d gpu accelerated spring mesh. This repo is made as a learning experience for me, don't take anything here as the ideal solution to a problem.

But hey, it makes pretty visuals.

## How to setup
* download unity editor
* clone repo
* open repo in unity
* open up SampleScene under Assets/Scenes

or 
* create an empty gameobjects
* assign the springmesh and impulse to said object
* assign the variables


## component rundown
Those are the components that the system uses, some of them are not in active use, but they exist in the repo nonetheless

### SpringMesh
This is the main component, it initialized the grid, points and springs while also controlling for spring and shader variables and shader execution. 

It creates 9 springs for each point, 8 springs with all 8 of the point's neighbours and 1 "fake" spring(initialPosition) that pulls the point to where it is supposed to exist in world coordinates. Feel free to disable diagonal springs / hor&vert springs to change grid behaviour.

public editor's variables:

* Density: how many points are generated per 1 unity distance
* Width and height: pretty self explanatory
* spring Constant: the spring's pull force
* damping force: how much the springs are damped, it prevents jitter
* Return force: the "spring constant" of the fake spring the pulls points to the origin
* Max Return Distance: After which distance (smaller) the return force acts
* Mesh Material: the material of the line mesh
* Min Velocity Gate: anti jitter variable that heavily damps spring forces lower than this

### Impulse
This is the force that mouse clicks add, it consists mostly of garbage commments of how it used to work, but currently it just sets computeshader variables based on mouse clicks.

There are two main variables, force strength and cutoff(radius), there's also a time factor that should affect the radius expansion rate untill it reaches the cutoff but currently doesn't do anything, feel free to implement it using the commented code as reference.


### DynamicMesh
The mesh that actually renders the points, currently i use MeshRenderer, feel free to change it, there are many better solutions.

### SpatialMap
once used by impulse to query points in a radius, now redundant.


## TODO
There are a lot of improvements that could be made if you're planning on using this code: 

The quicked "quick win" is to eliminate the gpu readback in the LateUpdate of SpringMesh. I already made most of the changes, the only ones missing is updatepositions and dynamic mesh allocation. This alone shold speed up the sim by 2x

You can make a custom shader instad of the grid to render & change color based on density, this can be pretty cool.

Many more that you'll discover when you enter the code.
