#!/usr/bin/env perl
# fix-closure-class.pl - Replace _003C_003Ec async state machine stubs with no-op lambdas.
# ILSpy decompiles async lambdas referencing the compiler-generated <>c singleton class,
# which doesn't exist in the decompiled source. Replace with async Task.CompletedTask.

use strict;
use warnings;

foreach my $file (@ARGV) {
    next unless -f $file;
    open(my $fh, '<', $file) or die "Cannot read $file: $!";
    my $content = do { local $/; <$fh> };
    close($fh);

    my $count = ($content =~ s/
        TyronThreadPool\.QueueTask\(
        \(System\.Func<System\.Threading\.Tasks\.Task>\)
        \(\[AsyncStateMachine\(typeof\(_003C_003Ec\.\w+\)\)\]\s*\(\)\s*=>\s*
        \{(?:[^{}]|\{[^{}]*\})*\}\),\s*
        "([^"]+)"\)
    /TyronThreadPool.QueueTask(async () => { await System.Threading.Tasks.Task.CompletedTask; }, "$1")/gsx);

    if ($count) {
        open(my $out, '>', $file) or die "Cannot write $file: $!";
        print $out $content;
        close($out);
        print "Replaced $count closure stub(s) in $file\n";
    }
}
