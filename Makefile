SCRIPTS := $(shell find . -name *.cs -and -not -name *.min.cs)
MIN_SCRIPTS := $(SCRIPTS:.cs=.min.cs)

%.min.cs: %.cs
	@echo Minifying $<...
	@sed '/ ==-- /q' $< | \
	    sed -e 's/@date@/$(shell date +%F)/' \
	    	-e 's/@revision@/$(shell git rev-parse HEAD)/' \
	    	> $@
	@echo >> $@
	@sed -n '/ ==-- /,$$p' $< | csmin >> $@

all: $(MIN_SCRIPTS)

clean:
	rm */*.min.cs

.PHONY: all clean
