# lovamap-wrapper

## About

This is a .NET minimal API intended to control access to the scientific computing tool LOVAMAP. It uses docker
to spin up containers on-demand that can execute LOVAMAP on a particular input file (.csv, soon will be expanded
to .json as well). Outputs are saved in an 'Output' folder on the parent directory. Job submissions are added
to a queue that are executed by a background service in FIFO order as resources become available. 