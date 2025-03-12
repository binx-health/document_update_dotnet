#!/bin/bash

# Start cron
service cron start

# Keep container running
tail -f /dev/null

